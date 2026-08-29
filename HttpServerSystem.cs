using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class ControlInput
	{
		public bool Left, Right, Up, Down, Jump, UseItem, UseTile;
		public int SelectedSlot = -1;
		public int SmartCursor = -1;
		public long Tick;
		public float Mx = float.NaN;
		public float My = float.NaN;
	}

	public class HttpServerSystem : ModSystem
	{
		public static volatile Snapshot LatestSnapshot;
		public static volatile ControlInput PendingControl;

		// /ws 用 WebSocket 不用 SSE:通道是双向的,agent 要能低延迟推打断回来。
		// 一个 socket 不能并发 SendAsync,所以每个客户端自带发送队列,PushEvent 只入队。
		sealed class WsClient
		{
			public System.Net.WebSockets.WebSocket Sock;
			public readonly System.Collections.Concurrent.BlockingCollection<string> Out =
				new System.Collections.Concurrent.BlockingCollection<string>();
		}
		private static readonly System.Collections.Generic.List<WsClient> _wsClients = new();
		private static readonly object _wsLock = new();

		public static void PushEvent(string type, string jsonBody = "{}")
		{
			string msg = "{\"type\":\"" + type + "\",\"data\":" + jsonBody + "}";
			lock (_wsLock)
				foreach (var c in _wsClients)
					try { c.Out.Add(msg); } catch { }
		}

		// messages received FROM the agent over the WebSocket (e.g. an interrupt / command). Drained on the main
		// thread if needed; for now the agent still commands via HTTP, this is the reverse push channel.
		public static readonly ConcurrentQueue<string> WsInbound = new();

		private static readonly ConcurrentQueue<(int src, int dst)> _swapQueue = new();
		// 传送:动玩家位置得在主线程做,HTTP 线程只入队
		private static readonly ConcurrentQueue<(int x, int y)> _tpQueue = new();
		// /hell_run {"teleport":true} 用:先落地狱再开跑
		private static readonly ConcurrentQueue<bool> _hellTpQueue = new();
		private static readonly ConcurrentQueue<(int tx, int ty)> _interactQueue = new();

		// mod 内部也要开箱(Unstick 采集),别再写第二条开箱路径
		public static void QueueInteract(int tx, int ty)
		{
			LastInteract = "pending";
			_interactQueue.Enqueue((tx, ty));
		}
		// 地狱流程要读地形、跑 Dijkstra 算线,几十毫秒 —— 绝不能在 HTTP 线程上干。
		// 排队交给主线程,和开箱/换位是同一套路子
		private static readonly ConcurrentQueue<bool> _hellRunQueue = new();
		public static string HellRunStart = "";   // 主线程跑完写这里,给 /hell_run_status 读
		// 上一次开箱的结果,给 /interact 的调用方看。以前拒绝是静默 continue,外面只知道"没开",不知道为什么。
		public static volatile string LastInteract = "idle";
		private static volatile bool _lootAllRequested;
		// 掏箱子前要腾几格。箱子 40 格但大多是零头,腾太多等于把建材白删了
		const int LootRoom = 8;
		private static volatile bool _quickHealRequested;

		private const string Prefix = "http://127.0.0.1:17878/";
		private HttpListener _listener;
		private Thread _thread;
		private volatile bool _running;
		private bool _announced;

		public override void PostUpdateEverything()
		{
			if (!_announced && _running)
			{
				if (Main.netMode == 2) { _announced = true; return; }
				if (Main.LocalPlayer == null || !Main.LocalPlayer.active) return;
				Main.NewText("[TerraBlind] HTTP server listening on " + Prefix, Color.LightGreen);
				_announced = true;
			}

			if (Main.LocalPlayer == null || !Main.LocalPlayer.active) return;

			PerceptionDiff.Tick();   // eye B-path: push salient world-change events to the agent (silent otherwise)
			SurvivalReflex.Tick();   // hand reflex: stay alive (jump out of lava / quick-heal) while any action runs

			// 【必须排在 _tpQueue 前面】:它往 _tpQueue 里塞坐标,排后面的话传送要等下一帧,
			// 而 StartHellRun 这一帧就按【传送前】的位置算线了
			while (_hellTpQueue.TryDequeue(out _))
			{
				var (htx, hty) = StateSnapshotPlayer.HellLanding();
				if (htx > 0) _tpQueue.Enqueue((htx, hty));
				else DiagLog.Write("[teleport] 地狱找不到落脚点");
			}
			while (_tpQueue.TryDequeue(out var tp))
			{
				var tpp = Main.LocalPlayer;
				if (tpp == null) continue;
				// 站在 (x,y) 这一格上:格心对齐,脚底贴着这一格的下沿
				tpp.position.X = tp.x * 16f + 8f - tpp.width / 2f;
				tpp.position.Y = (tp.y + 1) * 16f - tpp.height;
				tpp.velocity = Microsoft.Xna.Framework.Vector2.Zero;
				tpp.fallStart = (int)(tpp.position.Y / 16f);   // 不清的话落地按"从出发点掉下来"算摔伤
				DiagLog.Write($"[teleport] → ({tp.x},{tp.y})");
			}
			while (_swapQueue.TryDequeue(out var swap))
			{
				int src = swap.src, dst = swap.dst;
				if (src < 0 || src > 57 || dst < 0 || dst > 57 || src == dst) continue;
				var inv = Main.LocalPlayer.inventory;
				(inv[src], inv[dst]) = (inv[dst], inv[src]);
			}
			while (_hellRunQueue.TryDequeue(out _))
			{
				HellRunStart = StateSnapshotPlayer.StartHellRun(out string hrw) ? "" : hrw;
				// 【人在空中就重新排队,别把请求丢了】。整条线按人当前位置算,空中那个坐标是半路的;
				// 等落地(通常几十帧)再算一次就对了。只对这一种原因重排 —— 别的失败是真失败
				if (HellRunStart.Contains("还在空中")) { _hellRunQueue.Enqueue(true); break; }
			}
			while (_interactQueue.TryDequeue(out var tile))
			{
				// 上一个箱子还开着就先关掉再开新的。原来直接拒绝,于是只要有箱子没关,
				// 后面每一个都开不了(vanilla 关箱就是 chest=-1 + FindRecipes)。
				if (Main.LocalPlayer.chest != -1)
				{
					Main.LocalPlayer.chest = -1;
					Recipe.FindRecipes();
				}
				// 陷阱箱(FakeContainers 441/468)在 Main.chest 里也有条目,FindChest 照样找得到,
				// 于是直接写 Player.chest 就开了 --- 而这条路绕过右键,引线根本不触发,是作弊。
				// 一律不开,连线的陷阱我们也处理不了。
				if (IsFakeChest(tile.tx, tile.ty)) { LastInteract = "trapped_chest"; continue; }
				int idx = Chest.FindChest(tile.tx, tile.ty);
				if (idx == -1) { LastInteract = "no_chest"; continue; }
				// 开箱直接写 Player.chest 绕过了右键那条路,而锁的判定就在那条路上 —— 以前上锁的箱子照开,是作弊。
				// 用 vanilla 自己的 IsLockedOrInUse,别抄 frameX 范围;它顺带覆盖了"别人正在用"。
				var ch = Main.chest[idx];
				if (ch == null) { LastInteract = "no_chest"; continue; }
				// 用箱子自己的锚点格判锁,不用传进来的那格 —— 箱子占 2×2,判据看 frameX,点右下角会读错。
				if (Chest.IsLocked(ch.x, ch.y)) { LastInteract = "locked"; continue; }
				if (Chest.UsingChest(idx) != -1) { LastInteract = "in_use"; continue; }
				LastInteract = "opened";
				Main.LocalPlayer.chest = idx;
				Main.LocalPlayer.chestX = tile.tx;
				Main.LocalPlayer.chestY = tile.ty;
				Main.playerInventory = true;
				Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuOpen);
			}
			if (_lootAllRequested)
			{
				_lootAllRequested = false;
				if (Main.LocalPlayer.chest != -1)
				{
					// 背包满了 LootAll 会把塞不下的【原样写回箱子】—— 人走了,东西还在里面。
					// 掏之前先按清单删掉没用的:一箱最多 40 件,腾够就不会漏
					KeepList.MakeRoom(LootRoom);
					Terraria.UI.ChestUI.LootAll();
				}
			}
			if (_quickHealRequested)
			{
				_quickHealRequested = false;
				Main.LocalPlayer.QuickHeal();
			}
		}

		public override void Load()
		{
			LatestSnapshot = null;
			try
			{
				_listener = new HttpListener();
				_listener.Prefixes.Add(Prefix);
				_listener.Start();
				_running = true;
				_thread = new Thread(Loop) { IsBackground = true, Name = "TerraBlindHttp" };
				_thread.Start();
				Mod.Logger.Info("TerraBlind HTTP server listening on " + Prefix);
			}
			catch (Exception e)
			{
				Mod.Logger.Error("TerraBlind failed to start HTTP server: " + e);
			}
		}

		public override void Unload()
		{
			_running = false;
			_announced = false;
			try { _listener?.Stop(); } catch { }
			try { _listener?.Close(); } catch { }
			_listener = null;
			try { _thread?.Join(500); } catch { }
			_thread = null;
			LatestSnapshot = null;
		}

		private static System.Collections.Generic.HashSet<(int, int)> ParseExcludedGoals(string rb)
		{
			var result = new System.Collections.Generic.HashSet<(int, int)>();
			var pairs = System.Text.RegularExpressions.Regex.Matches(rb, "\\[(-?\\d+),(-?\\d+)\\]");
			bool inExcluded = false;
			int excludedIdx = rb.IndexOf("\"excluded_goals\"");
			if (excludedIdx < 0) return result;
			foreach (System.Text.RegularExpressions.Match p in pairs)
			{
				if (p.Index > excludedIdx)
					result.Add((int.Parse(p.Groups[1].Value), int.Parse(p.Groups[2].Value)));
			}
			return result;
		}

		private void Loop()
		{
			while (_running && _listener != null)
			{
				HttpListenerContext ctx;
				try
				{
					ctx = _listener.GetContext();
				}
				catch
				{
					break;
				}
				// WebSocket event channel: upgrade and hand off to a dedicated thread; the connection lives until
				// the client disconnects. Everything else is a normal HTTP request → Handle.
				if (ctx.Request.IsWebSocketRequest && ctx.Request.Url.AbsolutePath == "/ws")
				{
					var c = ctx;
					new Thread(() => ServeWebSocket(c)) { IsBackground = true, Name = "TerraBlindWs" }.Start();
					continue;
				}
				try
				{
					Handle(ctx);
				}
				catch (Exception e)
				{
					try
					{
						ctx.Response.StatusCode = 500;
						ctx.Response.Close();
					}
					catch { }
					Mod.Logger.Warn("TerraBlind request error: " + e.Message);
				}
			}
		}

		// Run one WebSocket client to completion: accept the upgrade, then pump a send loop (drains the client's
		// Out queue) and a receive loop (agent→game messages into WsInbound) until either side closes.
		private void ServeWebSocket(HttpListenerContext ctx)
		{
			System.Net.WebSockets.HttpListenerWebSocketContext wsCtx;
			try { wsCtx = ctx.AcceptWebSocketAsync(null).GetAwaiter().GetResult(); }
			catch { try { ctx.Response.Close(); } catch { } return; }
			var sock = wsCtx.WebSocket;
			var client = new WsClient { Sock = sock };
			lock (_wsLock) _wsClients.Add(client);
			try
			{
				client.Out.Add("{\"type\":\"hello\",\"data\":{}}");
				// send loop
				var sender = Task.Run(async () =>
				{
					try
					{
						foreach (var msg in client.Out.GetConsumingEnumerable())
						{
							var buf = Encoding.UTF8.GetBytes(msg);
							await sock.SendAsync(new ArraySegment<byte>(buf),
								System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
						}
					}
					catch { }
				});
				// receive loop (blocks this thread until close)
				var rbuf = new byte[4096];
				while (sock.State == System.Net.WebSockets.WebSocketState.Open)
				{
					var res = sock.ReceiveAsync(new ArraySegment<byte>(rbuf), System.Threading.CancellationToken.None)
						.GetAwaiter().GetResult();
					if (res.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
					if (res.Count > 0) WsInbound.Enqueue(Encoding.UTF8.GetString(rbuf, 0, res.Count));
				}
			}
			catch { }
			finally
			{
				lock (_wsLock) _wsClients.Remove(client);
				try { client.Out.CompleteAdding(); } catch { }
				try { sock.Abort(); } catch { }
			}
		}

		private void Handle(HttpListenerContext ctx)
		{
			string path = ctx.Request.Url.AbsolutePath;
			string body;
			int status = 200;

			if (path == "/state")
			{
				body = StateSerializer.ToJson(LatestSnapshot);
			}
			else if (path == "/trace")
			{
				// 逐帧追踪开关。默认关 —— 开着的时候候选表/邻居每行几千字符,真正的事件全被淹掉。
				string rb = ReadBody(ctx).Replace(" ", "");
				if (rb.Contains("\"on\":true")) DiagLog.Trace = true;
				else if (rb.Contains("\"on\":false")) DiagLog.Trace = false;
				body = "{\"trace\":" + (DiagLog.Trace ? "true" : "false") + "}";
			}
			else if (path == "/events_clear")
			{
				EventLog.Clear();
				body = "{\"ok\":true}";
			}
			else if (path == "/digtable")
			{
				DigTableSystem.Pending = true;   // Dump runs on the main thread (PostUpdateEverything) to read tiles safely
				body = "{\"ok\":true}";
			}
			else if (path == "/cursor")
			{
				var p = Main.LocalPlayer;
				if (p != null && p.active)
				{
					float mx = (Main.mouseX + Main.screenPosition.X - p.position.X - p.width / 2f) / 16f;
					float my = (Main.mouseY + Main.screenPosition.Y - p.position.Y - p.height / 2f) / 16f;
					int tx = (int)((Main.mouseX + Main.screenPosition.X) / 16f);
					int ty = (int)((Main.mouseY + Main.screenPosition.Y) / 16f);
					body = $"{{\"mx\":{mx:F3},\"my\":{my:F3},\"tile_x\":{tx},\"tile_y\":{ty}}}";
				}
				else body = "{\"error\":\"no_player\"}";
			}
			else if (path == "/mine_reach")
			{
				// 能挖到的矩形。tML 去掉了 GetTileRegion,所以逐格用 vanilla 自己 gate 挥镐的 IsInTileInteractionRange,取包围盒。
				// 范围来自 tileRangeX/Y+tileBoost+blockRange,和镐无关 —— 镐只决定挖不挖得动。
				var pr = Main.LocalPlayer;
				if (pr != null && pr.active)
				{
					int tb = (pr.HeldItem != null ? pr.HeldItem.tileBoost : 0) + pr.blockRange;
					int cx = (int)((pr.position.X + pr.width / 2f) / 16f);
					int cy = (int)((pr.position.Y + pr.height / 2f) / 16f);
					int scan = Player.tileRangeX + Player.tileRangeY + tb + 4;  // tb kept for scan margin
					int lx = int.MaxValue, ly = int.MaxValue, hx = int.MinValue, hy = int.MinValue;
					for (int x = cx - scan; x <= cx + scan; x++)
						for (int y = cy - scan; y <= cy + scan; y++)
							if (pr.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple))
							{
								if (x < lx) lx = x; if (x > hx) hx = x;
								if (y < ly) ly = y; if (y > hy) hy = y;
							}
					if (hx < lx) body = "{\"error\":\"no_reach\"}";
					else body = $"{{\"lx\":{lx},\"ly\":{ly},\"hx\":{hx},\"hy\":{hy}}}";
				}
				else body = "{\"error\":\"no_player\"}";
			}
			else if (path == "/swap")
			{
				var qs = ctx.Request.QueryString;
				if (int.TryParse(qs["src"], out int src) && int.TryParse(qs["dst"], out int dst))
				{
					_swapQueue.Enqueue((src, dst));
					body = "{\"ok\":true,\"src\":" + src + ",\"dst\":" + dst + "}";
				}
				else
				{
					body = "{\"error\":\"bad_params\",\"usage\":\"GET /swap?src=15&dst=0\"}";
					status = 400;
				}
			}
			else if (path == "/fight")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				if (rb.Contains("\"active\":false"))
					FightCoordinator.Stop();
				else
				{
					var distm = System.Text.RegularExpressions.Regex.Match(rb, "\"max_dist\":([0-9.]+)");
					float maxDist = distm.Success ? float.Parse(distm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 20f;
					FightCoordinator.Start(maxDist);
				}
				body = "{\"ok\":true}";
			}
			else if (path == "/fight_active")
			{
				body = "{\"active\":" + (FightCoordinator.IsActive ? "true" : "false") + "}";
			}
			else if (path == "/loot_all")
			{
				_lootAllRequested = true;
				body = "{\"ok\":true}";
			}
			else if (path == "/quick_heal")
			{
				_quickHealRequested = true;
				body = "{\"ok\":true}";
			}
			else if (path == "/control")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var ci = new ControlInput();
				ci.SelectedSlot = -1;
				var rb = reqBody.Replace(" ", "");
				if (rb.Contains("\"left\":true")) ci.Left = true;
				if (rb.Contains("\"right\":true")) ci.Right = true;
				if (rb.Contains("\"up\":true")) ci.Up = true;
				if (rb.Contains("\"down\":true")) ci.Down = true;
				if (rb.Contains("\"jump\":true")) ci.Jump = true;
				if (rb.Contains("\"jump_place\":true")) { StateSnapshotPlayer.JumpPlaceEnabled = true; }
				if (rb.Contains("\"use_item\":true")) ci.UseItem = true;
				if (rb.Contains("\"use_tile\":true")) ci.UseTile = true;
				var slotMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"selected_slot\"\\s*:\\s*(\\d+)");
				if (slotMatch.Success) ci.SelectedSlot = int.Parse(slotMatch.Groups[1].Value);
				var mxm = System.Text.RegularExpressions.Regex.Match(rb, "\"mx\":(-?[0-9.]+)");
				var mym = System.Text.RegularExpressions.Regex.Match(rb, "\"my\":(-?[0-9.]+)");
				if (mxm.Success) ci.Mx = float.Parse(mxm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
				if (mym.Success) ci.My = float.Parse(mym.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
				var scm2 = System.Text.RegularExpressions.Regex.Match(rb, "\"sc\":(\\d+)");
				if (scm2.Success) ci.SmartCursor = int.Parse(scm2.Groups[1].Value);
				ci.Tick = (long)Main.GameUpdateCount;
				PendingControl = ci;
				body = "{\"ok\":true}";
			}
			else if (path == "/interact")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var txMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"tile_x\"\\s*:\\s*(-?\\d+)");
				var tyMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"tile_y\"\\s*:\\s*(-?\\d+)");
				if (txMatch.Success && tyMatch.Success)
				{
					int tx = int.Parse(txMatch.Groups[1].Value);
					int ty = int.Parse(tyMatch.Groups[1].Value);
					// 排队,主线程才真去开。锁着/有人在用会被拒,结果在 /state 的 last_interact 里读。
					LastInteract = "pending";
					_interactQueue.Enqueue((tx, ty));
					body = "{\"ok\":true,\"tile_x\":" + tx + ",\"tile_y\":" + ty + ",\"note\":\"read last_interact from /state\"}";
				}
				else
				{
					body = "{\"error\":\"bad_params\",\"usage\":\"POST /interact {\\\"tile_x\\\":N,\\\"tile_y\\\":N}\"}";
					status = 400;
				}
			}
			else if (path == "/place")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var dxMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"dx\"\\s*:\\s*(-?\\d+)");
				var dyMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"dy\"\\s*:\\s*(-?\\d+)");
				var slotMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"slot\"\\s*:\\s*(\\d+)");
				var durMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"duration_frames\"\\s*:\\s*(\\d+)");
				if (dxMatch.Success && dyMatch.Success && slotMatch.Success && durMatch.Success)
				{
					PlaceCoordinator.Start(new PlaceRequest
					{
						Dx = int.Parse(dxMatch.Groups[1].Value),
						Dy = int.Parse(dyMatch.Groups[1].Value),
						Slot = int.Parse(slotMatch.Groups[1].Value),
						RemainingFrames = int.Parse(durMatch.Groups[1].Value),
					});
					body = "{\"ok\":true}";
				}
				else
				{
					body = "{\"error\":\"bad_params\",\"usage\":\"POST /place {dx,dy,slot,duration_frames,smart_cursor?}\"}";
					status = 400;
				}
			}
			else if (path == "/place_stop")
			{
				PlaceCoordinator.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/mine")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var dirM = System.Text.RegularExpressions.Regex.Match(rb, "\"dir\"\\s*:\\s*\"(left|right|up|down)\"");
				var txM = System.Text.RegularExpressions.Regex.Match(rb, "\"target_wx\"\\s*:\\s*(-?\\d+)");
				var tyM = System.Text.RegularExpressions.Regex.Match(rb, "\"target_wy\"\\s*:\\s*(-?\\d+)");
				if (dirM.Success && txM.Success && tyM.Success)
				{
					var dir = dirM.Groups[1].Value switch {
						"left" => MineDir.Left, "right" => MineDir.Right,
						"up" => MineDir.Up, _ => MineDir.Down };
					var mp = Main.LocalPlayer;
					MineCoordinator.Start(new MineRequest {
						Dir = dir,
						StartWx = (int)(mp.Center.X / 16f),
						StartWy = (int)((mp.position.Y + mp.height) / 16f) - 1,
						TargetWx = int.Parse(txM.Groups[1].Value),
						TargetWy = int.Parse(tyM.Groups[1].Value),
					});
					body = "{\"ok\":true}";
				}
				else
				{
					body = "{\"error\":\"bad_params\",\"usage\":\"POST /mine {\\\"dir\\\":\\\"down\\\",\\\"target_wx\\\":N,\\\"target_wy\\\":N}\"}";
					status = 400;
				}
			}
			else if (path == "/mine_stop")
			{
				MineCoordinator.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/item_use")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var wxM = System.Text.RegularExpressions.Regex.Match(rb, "\"target_wx\"\\s*:\\s*(-?\\d+)");
				var wyM = System.Text.RegularExpressions.Regex.Match(rb, "\"target_wy\"\\s*:\\s*(-?\\d+)");
				if (wxM.Success && wyM.Success)
				{
					var slotM = System.Text.RegularExpressions.Regex.Match(rb, "\"slot\"\\s*:\\s*(-?\\d+)");
					var durM  = System.Text.RegularExpressions.Regex.Match(rb, "\"duration_ticks\"\\s*:\\s*(\\d+)");
					ItemUseCoordinator.Start(new ItemUseRequest {
						TargetWx      = int.Parse(wxM.Groups[1].Value),
						TargetWy      = int.Parse(wyM.Groups[1].Value),
						Slot          = slotM.Success ? int.Parse(slotM.Groups[1].Value) : -1,
						DurationTicks = durM.Success  ? int.Parse(durM.Groups[1].Value)  : 0,
						Strict        = rb.Contains("\"strict\":true"),
					});
					body = "{\"ok\":true}";
				}
				else
				{
					body = "{\"error\":\"bad_params\",\"usage\":\"POST /item_use {\\\"target_wx\\\":N,\\\"target_wy\\\":N,\\\"slot\\\":N,\\\"duration_ticks\\\":N}\"}";
					status = 400;
				}
			}
			else if (path == "/item_use_stop")
			{
				ItemUseCoordinator.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/item_use_status")
			{
				// running | removed (collect target gone) | timeout (swings ran out, tile still there) | stopped | n/a
				body = "{\"active\":" + (ItemUseCoordinator.IsActive ? "true" : "false")
					+ ",\"outcome\":\"" + JsonEsc(ItemUseCoordinator.Outcome) + "\""
					+ ",\"reason\":\"" + JsonEsc(ItemUseCoordinator.Reason) + "\""
					+ ",\"snapped_wx\":" + ItemUseCoordinator.SnappedWx
					+ ",\"snapped_wy\":" + ItemUseCoordinator.SnappedWy
					+ ",\"target\":" + ItemUseCoordinator.TargetJson() + "}";
			}
			else if (path == "/walk_to_edge")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var dirMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"direction\"\\s*:\\s*\"([^\"]+)\"");
				var extraMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"extra_tiles\"\\s*:\\s*([0-9.]+)");
				bool dirRight = !dirMatch.Success || dirMatch.Groups[1].Value != "left";
				float extraTiles = extraMatch.Success ? float.Parse(extraMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 2f;
				WalkCoordinator.Start(dirRight, extraTiles);
				body = "{\"ok\":true}";
			}
			else if (path == "/walk_to_edge_stop")
			{
				WalkCoordinator.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/jump")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var dirMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"direction\"\\s*:\\s*\"([^\"]+)\"");
				var lxMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"launch_x\"\\s*:\\s*(-?[0-9.]+)");
				var txMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"target_x\"\\s*:\\s*(-?[0-9.]+)");
				bool dirRight = !dirMatch.Success || dirMatch.Groups[1].Value != "left";
				float launchX = lxMatch.Success ? float.Parse(lxMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0f;
				float targetX = txMatch.Success ? float.Parse(txMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : launchX;
				JumpCoordinator.Start(dirRight, launchX, targetX);
				body = "{\"ok\":true}";
			}
			else if (path == "/jump_stop")
			{
				JumpCoordinator.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/jump_done")
			{
				body = !JumpCoordinator.IsActive ? "{\"done\":true}" : "{\"done\":false}";
			}
			else if (path == "/jump_envelope")
			{
				var p = Main.LocalPlayer;
				if (p == null || !p.active)
				{
					body = "{\"error\":\"no_player\"}";
					status = 503;
				}
				else
				{
					float js = Player.jumpSpeed;
					float grav = p.gravity > 0f ? p.gravity : 0.4f;
					int jh = Player.jumpHeight;
					float vx = Math.Max(p.maxRunSpeed, p.accRunSpeed);
					float maxFall = p.maxFallSpeed;
					int tileSize = 16;

					float holdSpeed = js - grav;
					float phase1Ticks = jh + 1;
					float phase2Ticks = holdSpeed / grav;
					float peakT = phase1Ticks + phase2Ticks;
					float peakRisePx = holdSpeed * phase1Ticks + holdSpeed * phase2Ticks - 0.5f * grav * phase2Ticks * phase2Ticks;

					int maxCols = 32;
					var sb2 = new StringBuilder();
					sb2.Append("{\"envelope\":[");
					for (int col = 0; col < maxCols; col++)
					{
						if (col > 0) sb2.Append(',');
						float t = col * tileSize / Math.Max(vx, 0.01f);
						float risePx;
						if (t <= phase1Ticks)
							risePx = holdSpeed * t;
						else if (t <= peakT)
						{
							float dt = t - phase1Ticks;
							risePx = holdSpeed * phase1Ticks + holdSpeed * dt - 0.5f * grav * dt * dt;
						}
						else
						{
							float dt = t - peakT;
							risePx = peakRisePx - 0.5f * grav * dt * dt;
						}
						int dy = (int)(-risePx / tileSize);
						sb2.Append(dy);
					}
					sb2.Append("],");
					sb2.Append("\"vx\":").Append(vx.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
					sb2.Append("\"jump_speed\":").Append(js.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
					sb2.Append("\"jump_height\":").Append(jh).Append(',');
					sb2.Append("\"gravity\":").Append(grav.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
					sb2.Append('}');
					body = sb2.ToString();
				}
			}
			else if (path == "/skill")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var nameMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"name\"\\s*:\\s*\"([^\"]+)\"");
				var dirMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"direction\"\\s*:\\s*\"([^\"]+)\"");
				if (nameMatch.Success)
				{
					string skillName = nameMatch.Groups[1].Value;
					bool dirRight = !dirMatch.Success || dirMatch.Groups[1].Value != "left";
					if (skillName == "pillar_jump")
					{
						var riseMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"rise_tiles\"\\s*:\\s*(\\d+)");
						int riseTiles = riseMatch.Success ? int.Parse(riseMatch.Groups[1].Value) : 8;
						SkillExecutor.StartPillarJump(dirRight, riseTiles);
						body = "{\"ok\":true,\"skill\":\"pillar_jump\",\"rise_tiles\":" + riseTiles + "}";
					}
					else if (skillName == "dig_down")
					{
						SkillExecutor.StartDigDown();
						body = "{\"ok\":true,\"skill\":\"dig_down\"}";
					}
					else if (skillName == "dig_left")
					{
						SkillExecutor.StartDigLeft();
						body = "{\"ok\":true,\"skill\":\"dig_left\"}";
					}
					else if (skillName == "dig_right")
					{
						SkillExecutor.StartDigRight();
						body = "{\"ok\":true,\"skill\":\"dig_right\"}";
					}
					else if (skillName == "dig_up")
					{
						SkillExecutor.StartDigUp();
						body = "{\"ok\":true,\"skill\":\"dig_up\"}";
					}
					else if (skillName == "stop")
					{
						SkillExecutor.Stop();
						body = "{\"ok\":true,\"skill\":\"stop\"}";
					}
					else
					{
						body = "{\"error\":\"unknown_skill\",\"name\":\"" + skillName + "\"}";
						status = 400;
					}
				}
				else
				{
					body = "{\"error\":\"bad_params\"}";
					status = 400;
				}
			}
			else if (path == "/replay")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var frames = new System.Collections.Generic.List<ReplayFrame>();
				var frameMatches = System.Text.RegularExpressions.Regex.Matches(reqBody, "\\{[^}]*\\}");
				foreach (System.Text.RegularExpressions.Match m in frameMatches)
				{
					var rb = m.Value.Replace(" ", "");
					var mxm = System.Text.RegularExpressions.Regex.Match(rb, "\"mx\":(-?[0-9.]+)");
					var mym = System.Text.RegularExpressions.Regex.Match(rb, "\"my\":(-?[0-9.]+)");
					var slotm = System.Text.RegularExpressions.Regex.Match(rb, "\"slot\":(\\d+)");
					var scm   = System.Text.RegularExpressions.Regex.Match(rb, "\"sc\":([01])");
					var reprm = System.Text.RegularExpressions.Regex.Match(rb, "\"repeat\":(\\d+)");
					int repeat = reprm.Success ? int.Parse(reprm.Groups[1].Value) : 1;
					var frame = new ReplayFrame
					{
						Left         = rb.Contains("\"left\":true"),
						Right        = rb.Contains("\"right\":true"),
						Up           = rb.Contains("\"up\":true"),
						Down         = rb.Contains("\"down\":true"),
						Jump         = rb.Contains("\"jump\":true"),
						UseItem      = rb.Contains("\"use_item\":true"),
						Grapple      = rb.Contains("\"grapple\":true"),
						UseAlt       = rb.Contains("\"use_alt\":true"),
						UseTile      = rb.Contains("\"use_tile\":true"),
						Mount        = rb.Contains("\"mount\":true"),
						SelectedSlot = slotm.Success ? int.Parse(slotm.Groups[1].Value) : -1,
						SmartCursor  = scm.Success   ? int.Parse(scm.Groups[1].Value)   : -1,
						Mx           = mxm.Success ? float.Parse(mxm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0f,
						My           = mym.Success ? float.Parse(mym.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0f,
					};
					for (int r = 0; r < repeat; r++) frames.Add(frame);
				}
				ReplaySystem.Load(frames);
				body = "{\"ok\":true,\"frames\":" + frames.Count + "}";
			}
			else if (path == "/replay_stop")
			{
				ReplaySystem.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/record_start")
			{
				RecordSystem.Start();
				body = "{\"ok\":true}";
			}
			else if (path == "/record_stop")
			{
				string recorded = RecordSystem.Stop();
				body = recorded;
			}
			else if (path == "/build_rec_start")
			{
				BuildRecorder.Start();
				body = "{\"ok\":true}";
			}
			else if (path == "/build_rec_stop")
			{
				body = BuildRecorder.Stop();
			}
			else if (path == "/build_replay_start")
			{
				// POST {ax?,ay?} → 启动 build_rec.json 的帧重放,锚点缺省=玩家脚下。
				// 整个建造(nav→place/mine→next)都在 BuildReplayer 里,Python 只负责触发和轮询。
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var axM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"ax\"\\s*:\\s*(-?\\d+)");
				var ayM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"ay\"\\s*:\\s*(-?\\d+)");
				int ax = axM.Success ? int.Parse(axM.Groups[1].Value) : -1;
				int ay = ayM.Success ? int.Parse(ayM.Groups[1].Value) : -1;
				if (!BuildReplayer.Start(ax, ay, out string why))
					body = "{\"ok\":false,\"reason\":\"" + JsonEsc(why) + "\"}";
				else
					body = "{\"ok\":true,\"events\":" + BuildReplayer.Total + ",\"conflicts\":" + BuildOverlay.ConflictCount + "}";
			}
			else if (path == "/build_replay_status")
			{
				body = BuildReplayer.StatusJson();
			}
			else if (path == "/build_replay_stop")
			{
				BuildReplayer.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/test_action")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var nm = System.Text.RegularExpressions.Regex.Match(reqBody, "\"name\"\\s*:\\s*\"([^\"]+)\"");
				var dm = System.Text.RegularExpressions.Regex.Match(rb, "\"dir\":(-?\\d+)");
				string aName = nm.Success ? nm.Groups[1].Value : "";
				int aDir = dm.Success ? int.Parse(dm.Groups[1].Value) : 0;
				StateSpacePlanner.RequestTestAction(aName, aDir);
				body = $"{{\"ok\":true,\"name\":\"{aName}\",\"dir\":{aDir}}}";
			}
			else if (path == "/craft")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var idMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"item_id\":(\\d+)");
				var nameMatch = System.Text.RegularExpressions.Regex.Match(reqBody, "\"item_name\"\\s*:\\s*\"([^\"]+)\"");
				var amtMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"amount\":(\\d+)");
				int amount = amtMatch.Success ? int.Parse(amtMatch.Groups[1].Value) : 1;
				int targetId = -1;
				if (idMatch.Success)
					targetId = int.Parse(idMatch.Groups[1].Value);
				else if (nameMatch.Success)
				{
					// 游戏是中文的,createItem.Name 返回中文,而 LLM 只会说英文名 —— 只认显示名英文一发必失败。
					// 所以再认 ItemID 的字段名(不随语言变),去掉空格,"Work Bench"→WorkBench。
					string rawName = nameMatch.Groups[1].Value;
					string targetName = rawName.ToLowerInvariant();
					int byInternal = -1;
					{
						string key = rawName.Replace(" ", "");
						foreach (var nm in new[] { rawName, key })
						{
							if (Terraria.ID.ItemID.Search.ContainsName(nm))
							{ byInternal = Terraria.ID.ItemID.Search.GetId(nm); break; }
						}
					}
					for (int ri = 0; ri < Main.numAvailableRecipes; ri++)
					{
						var r = Main.recipe[Main.availableRecipe[ri]];
						if ((r.createItem.Name ?? "").ToLowerInvariant() == targetName
							|| (byInternal >= 0 && r.createItem.type == byInternal))
						{
							targetId = r.createItem.type;
							break;
						}
					}
				}
				if (targetId < 0)
				{
					var sb2 = new System.Text.StringBuilder();
					sb2.Append("{\"error\":\"item_not_found\",\"name_matched\":").Append(nameMatch.Success.ToString().ToLower());
					sb2.Append(",\"available_count\":").Append(Main.numAvailableRecipes);
					// 背包满时游戏不把任何配方算作 available,available_count=0 看着像"没材料"
					sb2.Append(",\"free_slots\":").Append(FreeSlots());
					sb2.Append(",\"raw_name\":\"").Append(nameMatch.Success ? nameMatch.Groups[1].Value : "").Append("\"");
					sb2.Append(",\"available_names\":[");
					for (int ri = 0; ri < Main.numAvailableRecipes; ri++)
					{
						if (ri > 0) sb2.Append(',');
						// 中文名(显示)和内部名(英文,可直接回传)一起给,LLM 失败一次就知道该发什么
					var cr = Main.recipe[Main.availableRecipe[ri]].createItem;
					string inm = Terraria.ID.ItemID.Search.ContainsId(cr.type) ? Terraria.ID.ItemID.Search.GetName(cr.type) : "";
					sb2.Append('"').Append(JsonEsc(cr.Name ?? ""));
					if (inm.Length > 0) sb2.Append('/').Append(JsonEsc(inm));
					sb2.Append('"');
					}
					sb2.Append("]}");
					body = sb2.ToString();
					status = 400;
				}
				else
				{
					int crafted = CraftCoordinator.Craft(targetId, amount);
					// crafted 只数真进背包的。要了 96 个只进来 0 个也是失败 —— 以前报 ok:true crafted:96,
					// 库存却是 0,错要到下一步才炸出来。
					string extra = ",\"wanted\":" + amount
						+ ",\"overflow\":" + CraftCoordinator.LastOverflow
						+ ",\"stop\":\"" + JsonEsc(CraftCoordinator.LastStop) + "\""
						+ ",\"free_slots\":" + FreeSlots();
					if (crafted >= amount)
						body = "{\"ok\":true,\"crafted\":" + crafted + extra + "}";
					else if (crafted > 0)
						body = "{\"ok\":false,\"error\":\"partial\",\"crafted\":" + crafted + extra + "}";
					else
						body = "{\"ok\":false,\"error\":\"" + (CraftCoordinator.LastStop == "inventory_full" ? "inventory_full" : "not_available")
							 + "\",\"crafted\":0,\"item_id\":" + targetId + extra + "}";
				}
			}
			else if (path == "/recipe")
			{
				// 查配方:要什么材料、站哪种台子、还差多少。craft 失败只说 not_available,不说缺什么。
				// 不限于"当前可合成" —— 没材料的照样能查,那才是查配方的意义。
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rnM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"name\"\\s*:\\s*\"([^\"]+)\"");
				if (!rnM.Success)
				{ body = "{\"error\":\"bad_request\"}"; status = 400; }
				else
				{
					string raw = rnM.Groups[1].Value;
					string low = raw.ToLowerInvariant();
					int want = -1;
					foreach (var nm in new[] { raw, raw.Replace(" ", "") })
						if (Terraria.ID.ItemID.Search.ContainsName(nm))
						{ want = Terraria.ID.ItemID.Search.GetId(nm); break; }
					var p2 = Main.LocalPlayer;
					var sbr = new System.Text.StringBuilder();
					int found = 0;
					sbr.Append("{\"recipes\":[");
					for (int ri = 0; ri < Recipe.numRecipes && found < 12; ri++)
					{
						var r = Main.recipe[ri];
						if (r == null || r.createItem == null || r.createItem.type <= 0) continue;
						bool hit = want >= 0 ? r.createItem.type == want
							: (r.createItem.Name ?? "").ToLowerInvariant() == low;
						if (!hit) continue;
						if (found > 0) sbr.Append(',');
						found++;
						string inm = Terraria.ID.ItemID.Search.ContainsId(r.createItem.type)
							? Terraria.ID.ItemID.Search.GetName(r.createItem.type) : "";
						sbr.Append("{\"makes\":\"").Append(JsonEsc(r.createItem.Name ?? ""))
						   .Append("\",\"internal\":\"").Append(JsonEsc(inm))
						   .Append("\",\"amount\":").Append(r.createItem.stack)
						   .Append(",\"ingredients\":[");
						for (int k = 0; k < r.requiredItem.Count; k++)
						{
							var ing = r.requiredItem[k];
							if (ing == null || ing.type <= 0) continue;
							if (k > 0) sbr.Append(',');
							// 背包里现有多少 —— 直接把"还差多少"算好,省得 LLM 自己对账
							int have = 0;
							if (p2 != null)
								foreach (var it in p2.inventory)
									if (it != null && !it.IsAir && it.type == ing.type) have += it.stack;
							string iinm = Terraria.ID.ItemID.Search.ContainsId(ing.type)
								? Terraria.ID.ItemID.Search.GetName(ing.type) : "";
							sbr.Append("{\"name\":\"").Append(JsonEsc(ing.Name ?? ""))
							   .Append("\",\"internal\":\"").Append(JsonEsc(iinm))
							   .Append("\",\"need\":").Append(ing.stack)
							   .Append(",\"have\":").Append(have).Append('}');
						}
						sbr.Append("],\"stations\":[");
						for (int k = 0; k < r.requiredTile.Count; k++)
						{
							int tt = r.requiredTile[k];
							if (tt < 0) continue;
							if (k > 0) sbr.Append(',');
							string tnm = Terraria.ID.TileID.Search.ContainsId(tt)
								? Terraria.ID.TileID.Search.GetName(tt) : tt.ToString();
							sbr.Append('"').Append(JsonEsc(tnm)).Append('"');
						}
						sbr.Append("]}");
					}
					sbr.Append("],\"found\":").Append(found).Append('}');
					body = sbr.ToString();
					if (found == 0) status = 404;
				}
			}
			else if (path == "/nav")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var gxM = System.Text.RegularExpressions.Regex.Match(rb, "\"gx\":(-?\\d+)");
				var gyM = System.Text.RegularExpressions.Regex.Match(rb, "\"gy\":(-?\\d+)");
				if (!gxM.Success || !gyM.Success)
				{
					body = "{\"ok\":false,\"reason\":\"bad_request\"}";
					status = 400;
				}
				else
				{
					int gxN = int.Parse(gxM.Groups[1].Value);
					int gyN = int.Parse(gyM.Groups[1].Value);
					// route single-point nav through the NEW StateSpacePlanner (physics-faithful) instead of the
					// legacy NavCoordinator. Execute plans + dispatches; ExecFailCode tells us if planning failed.
					var ssr = StateSpacePlanner.Execute(gxN, gyN);
					if (ssr.Found)
						body = "{\"ok\":true,\"goal\":[" + gxN + "," + gyN + "]}";
					else
					{
						string code = string.IsNullOrEmpty(StateSpacePlanner.ExecFailCode) ? "unreachable" : StateSpacePlanner.ExecFailCode;
						body = "{\"ok\":false,\"reason\":\"" + code + "\"}";
						status = 400;
					}
				}
			}
			else if (path == "/nav_unlimited")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var gxM = System.Text.RegularExpressions.Regex.Match(rb, "\"gx\":(-?\\d+)");
				var gyM = System.Text.RegularExpressions.Regex.Match(rb, "\"gy\":(-?\\d+)");
				if (!gxM.Success || !gyM.Success)
				{
					body = "{\"ok\":false,\"reason\":\"bad_request\"}";
					status = 400;
				}
				else
				{
					int gxN = int.Parse(gxM.Groups[1].Value);
					int gyN = int.Parse(gyM.Groups[1].Value);
					NavCoordinator.StartTo(gxN, gyN);
					body = "{\"ok\":true,\"goal\":[" + gxN + "," + gyN + "],\"unlimited\":true}";
				}
			}
			else if (path == "/nav_start")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var signMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"sign\"\\s*:\\s*(-?1)");
				int navSign = signMatch.Success ? int.Parse(signMatch.Groups[1].Value) : 1;
				// direction explore now runs on the NEW StateSpacePlanner via ExploreCoordinator (was NavCoordinator).
				ExploreCoordinator.Start(navSign);
				body = "{\"ok\":true}";
			}
			else if (path == "/nav_set_path")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var signMatch = System.Text.RegularExpressions.Regex.Match(reqBody.Replace(" ", ""), "\"sign\"\\s*:\\s*(-?1)");
				int navSign = signMatch.Success ? int.Parse(signMatch.Groups[1].Value) : 1;
				var nodes = NavCoordinator.ParsePathPublic(reqBody);
				if (nodes != null && nodes.Count > 0)
				{
					NavCoordinator.SetPath(navSign, nodes);
					body = $"{{\"ok\":true,\"nodes\":{nodes.Count}}}";
				}
				else
				{
					body = "{\"ok\":false,\"error\":\"no nodes parsed\"}";
				}
			}
			else if (path == "/nav_stop")
			{
				ExploreCoordinator.Stop();   // direction explore (new); also stops the SSP leg it dispatched
				NavCoordinator.Stop();       // legacy, in case anything still drives it
				StateSpacePlanner.StopExec();
				body = "{\"ok\":true}";
			}
			else if (path == "/sim_jump")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				float simPx = float.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"px\":(-?[\\d.]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
				float simPy = float.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"py\":(-?[\\d.]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
				float simVx = float.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"vx\":(-?[\\d.]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
				int simHold = int.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"hold\":(\\d+)").Groups[1].Value);
				int simSign = System.Text.RegularExpressions.Regex.Match(rb, "\"sign\":(-?1)").Success ? int.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"sign\":(-?1)").Groups[1].Value) : 1;
				var simVyMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"vy\":(-?[\\d.]+)");
				float simVy = simVyMatch.Success ? float.Parse(simVyMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0f;
				var simGroundedMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"grounded\":(true|false)");
				bool simGrounded = !simGroundedMatch.Success || simGroundedMatch.Groups[1].Value == "true";
				var simStart = new PhysicsSimulator.State { Px = simPx, Py = simPy, Vx = simVx, Vy = simVy, Grounded = simGrounded, JumpFramesLeft = simHold };
				var simResult = PhysicsSimulator.SimulateJump(simStart, simSign, simHold);
				var sb2 = new System.Text.StringBuilder();
				sb2.Append("{\"landed\":").Append(simResult.Landed ? "true" : "false");
				sb2.Append(",\"cx\":").Append(simResult.Cx).Append(",\"cy\":").Append(simResult.Cy);
				sb2.Append(",\"end_px\":").Append(simResult.EndState.Px.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
				sb2.Append(",\"end_py\":").Append(simResult.EndState.Py.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
				sb2.Append(",\"end_vx\":").Append(simResult.EndState.Vx.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
				sb2.Append(",\"end_vy\":").Append(simResult.EndState.Vy.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
				sb2.Append(",\"end_grounded\":").Append(simResult.EndState.Grounded ? "true" : "false");
				sb2.Append(",\"min_py\":").Append(simResult.MinPy.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
				sb2.Append(",\"frames\":[");
				var s2 = simStart;
				for (int fi = 0; fi < simResult.Frames.Count; fi++)
				{
					if (fi > 0) sb2.Append(',');
					var inp = simResult.Frames[fi];
					var ns = PhysicsSimulator.Step(s2, inp);
					int fcx = (int)((ns.Px + PhysicsSimulator.PlayerW / 2f) / 16);
					int fcy = (int)((ns.Py + PhysicsSimulator.PlayerH) / 16);
					sb2.Append($"{{\"f\":{fi},\"px\":{ns.Px.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)},\"py\":{ns.Py.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)},\"vx\":{ns.Vx.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)},\"vy\":{ns.Vy.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)},\"cx\":{fcx},\"cy\":{fcy},\"g\":{(ns.Grounded ? 1 : 0)}}}");
					s2 = ns;
				}
				sb2.Append("]}");
				body = sb2.ToString();
			}
			else if (path == "/start_seg_nav")
			{
				string reqBodySN;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBodySN = sr.ReadToEnd();
				var rbSN = reqBodySN.Replace(" ", "");
				int gxSN = int.Parse(System.Text.RegularExpressions.Regex.Match(rbSN, "\"gx\":(-?\\d+)").Groups[1].Value);
				int gySN = int.Parse(System.Text.RegularExpressions.Regex.Match(rbSN, "\"gy\":(-?\\d+)").Groups[1].Value);
				SegmentedNavCoordinator.StartTo(gxSN, gySN);
				body = "{\"ok\":true}";
			}
			else if (path == "/stop_seg_nav")
			{
				SegmentedNavCoordinator.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/debug_segment_plan")
			{
				string reqBodyS;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBodyS = sr.ReadToEnd();
				var rbS = reqBodyS.Replace(" ", "");
				int gxS = int.Parse(System.Text.RegularExpressions.Regex.Match(rbS, "\"gx\":(-?\\d+)").Groups[1].Value);
				int gyS = int.Parse(System.Text.RegularExpressions.Regex.Match(rbS, "\"gy\":(-?\\d+)").Groups[1].Value);
				var mR = System.Text.RegularExpressions.Regex.Match(rbS, "\"radius\":(\\d+)");
				int radiusS = mR.Success ? int.Parse(mR.Groups[1].Value) : 25;
				body = PathPlanner.PlanToWindowed(gxS, gyS, radiusS);
				var segNodes = NavCoordinator.ParsePathPublic(body);
				PathVisSystem.SetPlanPath(segNodes, PathPlanner.GetEnvelopeCache());
			}
			else if (path == "/ss_plan")
			{
				string rbBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					rbBody = sr.ReadToEnd();
				var rb2 = rbBody.Replace(" ", "");
				int gx2 = int.Parse(System.Text.RegularExpressions.Regex.Match(rb2, "\"gx\":(-?\\d+)").Groups[1].Value);
				int gy2 = int.Parse(System.Text.RegularExpressions.Regex.Match(rb2, "\"gy\":(-?\\d+)").Groups[1].Value);
				var ssr = StateSpacePlanner.Plan(gx2, gy2);
				StateSpacePlanner.Visualize(ssr, gx2, gy2);
				var sb2 = new System.Text.StringBuilder();
				sb2.Append("{\"found\":").Append(ssr.Found ? "true" : "false");
				sb2.Append(",\"expansions\":").Append(ssr.Expansions);
				sb2.Append(",\"millis\":").Append(ssr.Millis.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
				sb2.Append(",\"path_len\":").Append(ssr.Path.Count);
				sb2.Append(",\"best_dx\":").Append(ssr.BestDx.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
				sb2.Append(",\"best_dy\":").Append(ssr.BestDy.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
				sb2.Append(",\"path\":[");
				for (int i = 0; i < ssr.Path.Count; i++)
				{
					if (i > 0) sb2.Append(',');
					sb2.Append('[').Append((int)ssr.Path[i].px).Append(',').Append((int)ssr.Path[i].py).Append(']');
				}
				sb2.Append("]}");
				body = sb2.ToString();
			}
			else if (path == "/debug_waypoints")
			{
				string reqBodyW;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBodyW = sr.ReadToEnd();
				var rbW = reqBodyW.Replace(" ", "");
				int gxW = int.Parse(System.Text.RegularExpressions.Regex.Match(rbW, "\"gx\":(-?\\d+)").Groups[1].Value);
				int gyW = int.Parse(System.Text.RegularExpressions.Regex.Match(rbW, "\"gy\":(-?\\d+)").Groups[1].Value);
				var pw = Main.LocalPlayer;
				if (pw == null) { body = "{\"error\":\"no_player\"}"; }
				else
				{
					int sxW = (int)((pw.position.X + pw.width / 2f) / 16f);
					int syW = (int)((pw.position.Y + pw.height) / 16f);
					var wps = WaypointPlanner.Generate(sxW, syW, gxW, gyW);
					var tilesW = new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>();
					foreach (var (wxW, wyW) in wps)
						tilesW.Add((wxW, wyW, new Microsoft.Xna.Framework.Color(255, 100, 255, 220)));
					PathVisSystem.SetTiles(tilesW, ttlFrames: 600);
					var sbW = new System.Text.StringBuilder();
					sbW.Append("{\"start\":[").Append(sxW).Append(',').Append(syW).Append("],\"waypoints\":[");
					for (int i = 0; i < wps.Count; i++)
					{
						if (i > 0) sbW.Append(',');
						sbW.Append('[').Append(wps[i].wx).Append(',').Append(wps[i].wy).Append(']');
					}
					sbW.Append("]}");
					body = sbW.ToString();
				}
			}
			else if (path == "/exec_jump_to")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				int ejTargetCx = int.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"target_cx\":(-?\\d+)").Groups[1].Value);
				int ejSign2 = System.Text.RegularExpressions.Regex.Match(rb, "\"sign\":(-?1)").Success ? int.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"sign\":(-?1)").Groups[1].Value) : 1;
				body = JumpExecutor.FindAndExecute(ejTargetCx, ejSign2);
			}
			else if (path == "/exec_jump")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				int ejHold = int.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"hold\":(\\d+)").Groups[1].Value);
				int ejSign = System.Text.RegularExpressions.Regex.Match(rb, "\"sign\":(-?1)").Success ? int.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"sign\":(-?1)").Groups[1].Value) : 1;
				var ejMsM = System.Text.RegularExpressions.Regex.Match(rb, "\"move_start\":(\\d+)");
				var ejMfM = System.Text.RegularExpressions.Regex.Match(rb, "\"move_frames\":(\\d+)");
				int ejMoveStart  = ejMsM.Success ? int.Parse(ejMsM.Groups[1].Value) : 0;
				int ejMoveFrames = ejMfM.Success ? int.Parse(ejMfM.Groups[1].Value) : ejHold;
				JumpExecutor.Start(ejHold, ejSign, ejMoveStart, ejMoveFrames);
				body = "{\"ok\":true}";
			}
			else if (path == "/exec_jump_result")
			{
				body = JumpExecutor.GetResult();
			}
			else if (path == "/test_plat_up")
			{
				var pp = Main.LocalPlayer;
				if (pp == null) { body = "{\"error\":\"no_player\"}"; }
				else
				{
					int slot = NavCoordinator.FindPlatformSlot(pp);
					if (slot < 0) { body = "{\"error\":\"no_platform_item\"}"; }
					else
					{
						var frames = PlatformExecutor.BuildPlatUpFrames(pp, slot);
						ReplaySystem.Load(frames);
						body = $"{{\"ok\":true,\"slot\":{slot},\"total_frames\":{frames.Count}}}";
					}
				}
			}
			else if (path == "/test_plat_up_n")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var nM = System.Text.RegularExpressions.Regex.Match(rb, "\"n\":(\\d+)");
				int n = nM.Success ? int.Parse(nM.Groups[1].Value) : 2;
				var pp = Main.LocalPlayer;
				if (pp == null) { body = "{\"error\":\"no_player\"}"; }
				else
				{
					int slot = NavCoordinator.FindPlatformSlot(pp);
					if (slot < 0) { body = "{\"error\":\"no_platform_item\"}"; }
					else
					{
						var seg = PlatformExecutor.BuildPlatUpFrames(pp, slot);
						var all = new System.Collections.Generic.List<ReplayFrame>();
						for (int i = 0; i < n; i++) all.AddRange(seg);
						ReplaySystem.Load(all);
						body = $"{{\"ok\":true,\"slot\":{slot},\"n\":{n},\"total_frames\":{all.Count}}}";
					}
				}
			}
			else if (path == "/test_plat_jump")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var signM = System.Text.RegularExpressions.Regex.Match(rb, "\"sign\":(-?1)");
				int sign = signM.Success ? int.Parse(signM.Groups[1].Value) : 1;
				var pp = Main.LocalPlayer;
				if (pp == null) { body = "{\"error\":\"no_player\"}"; }
				else
				{
					int slot = NavCoordinator.FindPlatformSlot(pp);
					if (slot < 0) { body = "{\"error\":\"no_platform_item\"}"; }
					else
					{
						var frames = PlatformExecutor.BuildPlatJumpFrames(pp, slot, sign, out int placeTx, out int placeTy, out int landFrame);
						ReplaySystem.Load(frames);
						body = $"{{\"ok\":true,\"slot\":{slot},\"land_frame\":{landFrame},\"place_tx\":{placeTx},\"place_ty\":{placeTy},\"total_frames\":{frames.Count}}}";
					}
				}
			}
			else if (path == "/test_plat_jump_n")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var nM = System.Text.RegularExpressions.Regex.Match(rb, "\"n\":(\\d+)");
				var signM2 = System.Text.RegularExpressions.Regex.Match(rb, "\"sign\":(-?1)");
				int n = nM.Success ? int.Parse(nM.Groups[1].Value) : 2;
				int sign2 = signM2.Success ? int.Parse(signM2.Groups[1].Value) : 1;
				body = PlatJumpExecutor.StartN(n, sign2);
			}
			else if (path == "/mark_placeable")
			{
				var p3 = Main.LocalPlayer;
				if (p3 == null) { body = "{\"error\":\"no_player\"}"; }
				else
				{
					int pcx = (int)((p3.position.X + p3.width / 2f) / 16f);
					int pcy = (int)((p3.position.Y + p3.height) / 16f);
					var tiles = new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>();
					for (int dx = -3; dx <= 3; dx++)
						for (int dy = -3; dy <= 3; dy++)
						{
							int tx = pcx + dx, ty = pcy + dy;
							if (PathPlanner.CanPlacePlatformAt(tx, ty))
								tiles.Add((tx, ty, new Microsoft.Xna.Framework.Color(0, 255, 180, 160)));
						}
					PathVisSystem.SetTiles(tiles, ttlFrames: 300);
					body = $"{{\"ok\":true,\"count\":{tiles.Count}}}";
				}
			}
			else if (path == "/debug_jump_edges")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				int dbgCx = int.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"cx\":(-?\\d+)").Groups[1].Value);
				int dbgCy = int.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"cy\":(-?\\d+)").Groups[1].Value);
				int dbgSign = System.Text.RegularExpressions.Regex.Match(rb, "\"sign\":(-?1)").Success ? int.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"sign\":(-?1)").Groups[1].Value) : 1;
				var p2 = Main.LocalPlayer;
				var edges = PathPlanner.DebugJumpEdges(p2, dbgCx, dbgCy, dbgSign);
				var sb3 = new System.Text.StringBuilder();
				sb3.Append("[");
				for (int ei = 0; ei < edges.Count; ei++)
				{
					if (ei > 0) sb3.Append(',');
					sb3.Append($"{{\"lx\":{edges[ei].lx},\"ly\":{edges[ei].ly},\"hold\":{edges[ei].hold}}}");
				}
				sb3.Append("]");
				body = sb3.ToString();
			}
			else if (path == "/debug_jump_edges_verbose")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				int dbgCx = int.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"cx\":(-?\\d+)").Groups[1].Value);
				int dbgCy = int.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"cy\":(-?\\d+)").Groups[1].Value);
				int dbgSign = System.Text.RegularExpressions.Regex.Match(rb, "\"sign\":(-?1)").Success ? int.Parse(System.Text.RegularExpressions.Regex.Match(rb, "\"sign\":(-?1)").Groups[1].Value) : 1;
				body = PathPlanner.DebugJumpEdgesVerbose(Main.LocalPlayer, dbgCx, dbgCy, dbgSign);
			}
			else if (path == "/nav_done")
			{
				// direction-explore (ExploreCoordinator) has priority: it walks forever (no 'done'), only ends on
				// explore_stuck. while exploring, per-leg SSP states are internal → report explore-level status.
				if (ExploreCoordinator.Active)
					body = "{\"done\":false,\"status\":\"running\"}";
				else if (!string.IsNullOrEmpty(ExploreCoordinator.FailCode))
					body = "{\"done\":false,\"status\":\"failed\",\"reason\":\"" + ExploreCoordinator.FailCode + "\"}";
				// else single-point /nav: read the StateSpacePlanner status machine.
				else if (StateSpacePlanner.ExecRunning)
					body = "{\"done\":false,\"status\":\"running\"}";
				else if (StateSpacePlanner.ExecDone)
					body = "{\"done\":true,\"status\":\"done\",\"reason\":\"done\"}";
				else
				{
					string code = string.IsNullOrEmpty(StateSpacePlanner.ExecFailCode) ? "unknown" : StateSpacePlanner.ExecFailCode;
					body = "{\"done\":false,\"status\":\"failed\",\"reason\":\"" + code + "\"}";
				}
			}
			else if (path == "/seg_nav_done")
			{
				if (SegmentedNavCoordinator.State == SegState.Done)
					body = "{\"done\":true,\"status\":\"done\",\"reason\":\"done\"}";
				else if (SegmentedNavCoordinator.State == SegState.Failed)
				{
					string code = string.IsNullOrEmpty(SegmentedNavCoordinator.FailCode) ? "unknown" : SegmentedNavCoordinator.FailCode;
					string ctxPart = string.IsNullOrEmpty(SegmentedNavCoordinator.FailCtxJson) ? "" : "," + SegmentedNavCoordinator.FailCtxJson;
					body = "{\"done\":false,\"status\":\"failed\",\"reason\":\"" + code + "\",\"legacy_reason\":\"" + SegmentedNavCoordinator.FailReason + "\"" + ctxPart + "}";
				}
				else if (SegmentedNavCoordinator.IsActive)
					body = "{\"done\":false,\"status\":\"running\"}";
				else
					body = "{\"done\":false,\"status\":\"idle\"}";
			}
			else if (path == "/nav_path")
			{
				body = NavCoordinator.GetPathJson();
			}
			else if (path == "/plan_path")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var signMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"sign\"\\s*:\\s*(-?1)");
				int planSign = signMatch.Success ? int.Parse(signMatch.Groups[1].Value) : 1;
				body = PathPlanner.Plan(planSign);
				var planNodes = NavCoordinator.ParsePathPublic(body);
				PathVisSystem.SetPlanPath(planNodes, PathPlanner.GetEnvelopeCache());
			}
			else if (path == "/path_vis")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var nodes = new System.Collections.Generic.List<(int wx, int wy)>();
				var matches = System.Text.RegularExpressions.Regex.Matches(reqBody, "\\[\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*\\]");
				foreach (System.Text.RegularExpressions.Match m in matches)
					nodes.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
				PathVisSystem.SetPath(nodes);
				body = "{\"ok\":true,\"nodes\":" + nodes.Count + "}";
			}
			else if (path == "/path_vis_blocks")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var pillar = new System.Collections.Generic.List<(int wx, int wy)>();
				var bridge = new System.Collections.Generic.List<(int wx, int wy)>();
				var matches = System.Text.RegularExpressions.Regex.Matches(reqBody, "\\[\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*\\]");
				bool inBridge = false;
				int bridgeIdx = reqBody.IndexOf("\"bridge\"");
				foreach (System.Text.RegularExpressions.Match m in matches)
				{
					if (!inBridge && bridgeIdx >= 0 && m.Index > bridgeIdx) inBridge = true;
					int wx = int.Parse(m.Groups[1].Value), wy = int.Parse(m.Groups[2].Value);
					if (inBridge) bridge.Add((wx, wy));
					else pillar.Add((wx, wy));
				}
				PathVisSystem.SetBlocks(pillar, bridge);
				body = "{\"ok\":true}";
			}
			else if (path == "/path_vis_tiles")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var parsed = System.Text.Json.JsonDocument.Parse(reqBody);
				var tiles = new System.Collections.Generic.List<(int wx, int wy, Microsoft.Xna.Framework.Color color)>();
				foreach (var item in parsed.RootElement.EnumerateArray())
				{
					int wx = item.GetProperty("wx").GetInt32();
					int wy = item.GetProperty("wy").GetInt32();
					int r = item.TryGetProperty("r", out var rp) ? rp.GetInt32() : 255;
					int g = item.TryGetProperty("g", out var gp) ? gp.GetInt32() : 255;
					int b = item.TryGetProperty("b", out var bp) ? bp.GetInt32() : 255;
					tiles.Add((wx, wy, new Microsoft.Xna.Framework.Color(r, g, b)));
				}
				PathVisSystem.SetTiles(tiles);
				body = "{\"ok\":true,\"tiles\":" + tiles.Count + "}";
			}
			else if (path == "/debug_labels")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var parsed = System.Text.Json.JsonDocument.Parse(reqBody);
				var labels = new System.Collections.Generic.List<(int wx, int wy, string text, Microsoft.Xna.Framework.Color color)>();
				foreach (var item in parsed.RootElement.EnumerateArray())
				{
					int wx = item.GetProperty("wx").GetInt32();
					int wy = item.GetProperty("wy").GetInt32();
					string text = item.GetProperty("text").GetString() ?? "";
					int r = item.TryGetProperty("r", out var rp) ? rp.GetInt32() : 255;
					int g = item.TryGetProperty("g", out var gp) ? gp.GetInt32() : 255;
					int b = item.TryGetProperty("b", out var bp) ? bp.GetInt32() : 255;
					labels.Add((wx, wy, text, new Microsoft.Xna.Framework.Color(r, g, b)));
				}
				PathVisSystem.SetLabels(labels);
				body = "{\"ok\":true,\"labels\":" + labels.Count + "}";
			}
			else if (path == "/freeze")
			{
				bool ok = FreezeSystem.Freeze();
				body = "{\"ok\":" + (ok ? "true" : "false") + ",\"frozen\":true}";
			}
			else if (path == "/unfreeze")
			{
				bool ok = FreezeSystem.Unfreeze();
				body = "{\"ok\":" + (ok ? "true" : "false") + ",\"frozen\":false}";
			}
			else if (path == "/inspect")
			{
				var p = Main.LocalPlayer;
				int pcx = p != null ? (int)((p.position.X + p.width / 2f) / 16f) : 0;
				int feetY = p != null ? (int)((p.position.Y + p.height) / 16f) : 0;
				float vx = p?.velocity.X ?? 0f;
				float vy = p?.velocity.Y ?? 0f;
				var recent = DiagLog.FlushWindow(60);
				var sb = new System.Text.StringBuilder();
				sb.Append("{\"tick\":").Append(Main.GameUpdateCount);
				sb.Append(",\"frozen\":").Append(FreezeSystem.IsFrozen ? "true" : "false");
				sb.Append(",\"nav_state\":\"").Append(NavCoordinator.State).Append("\"");
				sb.Append(",\"px\":").Append(pcx).Append(",\"py\":").Append(feetY);
				sb.Append(",\"vx\":").Append(vx.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
				sb.Append(",\"vy\":").Append(vy.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
				sb.Append(",\"recent_events\":[");
				for (int i = 0; i < recent.Count; i++)
				{
					if (i > 0) sb.Append(',');
					sb.Append(recent[i]);
				}
				sb.Append("]}");
				body = sb.ToString();
			}
			else if (path == "/step_node")
			{
				bool ok = FreezeSystem.StepFrame();
				body = "{\"ok\":" + (ok ? "true" : "false") + "}";
			}
			else if (path == "/continue")
			{
				FreezeSystem.Unfreeze();
				body = "{\"ok\":true}";
			}
			else if (path == "/breakpoint_set")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var idM = System.Text.RegularExpressions.Regex.Match(rb, "\"id\":\"([^\"]+)\"");
				var onM = System.Text.RegularExpressions.Regex.Match(rb, "\"on\":\"([^\"]+)\"");
				if (idM.Success && onM.Success)
				{
					var bp = new Breakpoint { Id = idM.Groups[1].Value, On = onM.Groups[1].Value };
					var valM = System.Text.RegularExpressions.Regex.Match(rb, "\"value\":\"([^\"]+)\"");
					if (valM.Success) bp.Value = valM.Groups[1].Value;
					var xM = System.Text.RegularExpressions.Regex.Match(rb, "\"x\":\\[(-?\\d+),(-?\\d+)\\]");
					var yM = System.Text.RegularExpressions.Regex.Match(rb, "\"y\":\\[(-?\\d+),(-?\\d+)\\]");
					if (xM.Success) { bp.X0 = int.Parse(xM.Groups[1].Value); bp.X1 = int.Parse(xM.Groups[2].Value); }
					if (yM.Success) { bp.Y0 = int.Parse(yM.Groups[1].Value); bp.Y1 = int.Parse(yM.Groups[2].Value); }
					var fieldM = System.Text.RegularExpressions.Regex.Match(rb, "\"field\":\"([^\"]+)\"");
					var thrM = System.Text.RegularExpressions.Regex.Match(rb, "\"threshold\":([\\d.]+)");
					var nM = System.Text.RegularExpressions.Regex.Match(rb, "\"n\":(\\d+)");
					if (fieldM.Success) bp.Field = fieldM.Groups[1].Value;
					if (thrM.Success) bp.Threshold = float.Parse(thrM.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
					if (nM.Success) bp.N = int.Parse(nM.Groups[1].Value);
					BreakpointSystem.Set(bp);
					body = "{\"ok\":true,\"id\":\"" + bp.Id + "\"}";
				}
				else body = "{\"error\":\"missing id or on\"}";
			}
			else if (path == "/breakpoint_clear")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var idM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"id\"\\s*:\\s*\"([^\"]+)\"");
				if (idM.Success) { BreakpointSystem.Clear(idM.Groups[1].Value); body = "{\"ok\":true}"; }
				else body = "{\"error\":\"missing id\"}";
			}
			else if (path == "/breakpoints")
			{
				var bps = BreakpointSystem.GetAll();
				var sb = new System.Text.StringBuilder();
				sb.Append("{\"breakpoints\":[");
				for (int i = 0; i < bps.Count; i++)
				{
					if (i > 0) sb.Append(',');
					sb.Append("{\"id\":\"").Append(bps[i].Id).Append("\",\"on\":\"").Append(bps[i].On).Append("\"}");
				}
				sb.Append("]}");
				body = sb.ToString();
			}
			else if (path == "/sim_test")
			{
				var p = Main.LocalPlayer;
				if (p == null || !p.active) { body = "{\"error\":\"no_player\"}"; status = 503; }
				else
				{
					string reqBody;
					using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
						reqBody = sr.ReadToEnd();
					var rb = reqBody.Replace(" ", "");
					var holdM = System.Text.RegularExpressions.Regex.Match(rb, "\"hold_frames\":(\\d+)");
					var dirM  = System.Text.RegularExpressions.Regex.Match(rb, "\"dir\":(-?1)");
					int holdFrames = holdM.Success ? int.Parse(holdM.Groups[1].Value) : 15;
					int dir = dirM.Success ? int.Parse(dirM.Groups[1].Value) : 1;
					var start = new PhysicsSimulator.State
					{
						Px = p.position.X, Py = p.position.Y,
						Vx = p.velocity.X, Vy = p.velocity.Y,
						Grounded = p.velocity.Y == 0f,
						JumpFramesLeft = holdFrames,
					};
					var result = PhysicsSimulator.SimulateJump(start, dir, holdFrames);
					var sb2 = new System.Text.StringBuilder();
					sb2.Append("{\"landed\":").Append(result.Landed ? "true" : "false");
					sb2.Append(",\"cx\":").Append(result.Cx).Append(",\"cy\":").Append(result.Cy);
					sb2.Append(",\"frames\":[");
					for (int i = 0; i < result.Frames.Count; i++)
					{
						if (i > 0) sb2.Append(',');
						sb2.Append(result.Frames[i].Jump ? "1" : "0");
					}
					sb2.Append("]}");
					body = sb2.ToString();
				}
			}
			else if (path == "/physics_record_start")
			{
				PhysicsRecorder.Start();
				body = "{\"ok\":true}";
			}
			else if (path == "/physics_record_stop")
			{
				body = PhysicsRecorder.Stop();
			}
			else if (path == "/terrain")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				int cx = 0, cy = 0, rw = 40, rh = 20;
				var mCx = System.Text.RegularExpressions.Regex.Match(rb, "\"cx\":(-?\\d+)");
				var mCy = System.Text.RegularExpressions.Regex.Match(rb, "\"cy\":(-?\\d+)");
				var mW  = System.Text.RegularExpressions.Regex.Match(rb, "\"w\":(\\d+)");
				var mH  = System.Text.RegularExpressions.Regex.Match(rb, "\"h\":(\\d+)");
				if (mCx.Success) cx = int.Parse(mCx.Groups[1].Value);
				if (mCy.Success) cy = int.Parse(mCy.Groups[1].Value);
				if (mW.Success)  rw = int.Parse(mW.Groups[1].Value);
				if (mH.Success)  rh = int.Parse(mH.Groups[1].Value);
				var p2 = Main.LocalPlayer;
				if (cx == 0 && cy == 0 && p2 != null)
				{
					cx = (int)((p2.position.X + p2.width / 2f) / 16f);
					cy = (int)((p2.position.Y + p2.height) / 16f);
				}
				var sb2 = new System.Text.StringBuilder();
				sb2.Append("{\"cx\":").Append(cx).Append(",\"cy\":").Append(cy)
				   .Append(",\"w\":").Append(rw).Append(",\"h\":").Append(rh)
				   .Append(",\"x0\":").Append(cx - rw / 2).Append(",\"y0\":").Append(cy - rh / 2)
				   .Append(",\"rows\":[");
				int x0 = cx - rw / 2, y0 = cy - rh / 2;
				for (int row = 0; row < rh; row++)
				{
					if (row > 0) sb2.Append(',');
					sb2.Append('"');
					for (int col = 0; col < rw; col++)
					{
						int wx = x0 + col, wy = y0 + row;
						if (wx < 0 || wy < 0 || wx >= Main.maxTilesX || wy >= Main.maxTilesY) { sb2.Append('?'); continue; }
						var t = Main.tile[wx, wy];
						if (t == null || !t.HasTile) { sb2.Append('.'); continue; }
						if (Predicates.IsWall(wx, wy)) sb2.Append('#');
						else if (Main.tileSolidTop[t.TileType]) sb2.Append('-');
						else sb2.Append('+');
					}
					sb2.Append('"');
				}
				sb2.Append("]}");
				body = sb2.ToString();
			}
			else if (path == "/health")
			{
				body = "{\"ok\":true}";
			}
			// ---- /act : the complete action primitive. steps run serially, fields inside a step run in parallel.
			// Every step MUST carry an `until` (no open-ended steps — a step that cannot end is a step that can hang).
			else if (path == "/act")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "").Replace("\n", "").Replace("\r", "").Replace("\t", "");
				var steps = new System.Collections.Generic.List<ActStep>();
				string err = null;
				// split the steps array on top-level object boundaries (steps are flat: one nesting level for
				// cursor/until/invariant, so brace counting is enough — no full JSON parser needed).
				int arr = rb.IndexOf("\"steps\":[");
				if (arr < 0) err = "no_steps";
				else
				{
					int i2 = arr + 9, depth = 0, start = -1;
					for (; i2 < rb.Length; i2++)
					{
						char c = rb[i2];
						if (c == '{') { if (depth == 0) start = i2; depth++; }
						else if (c == '}') { depth--; if (depth == 0 && start >= 0) { steps.Add(ParseActStep(rb.Substring(start, i2 - start + 1))); start = -1; } }
						else if (c == ']' && depth == 0) break;
					}
					if (steps.Count == 0) err = "no_steps";
					foreach (var st in steps)
						if (st == null || st.UntilKind.Length == 0) { err = "step_missing_until"; break; }
				}
				if (err != null) { body = "{\"ok\":false,\"reason\":\"" + err + "\"}"; status = 400; }
				else
				{
					var toM = System.Text.RegularExpressions.Regex.Match(rb, "\"timeout_frames\":(\\d+)");
					int tf = toM.Success ? int.Parse(toM.Groups[1].Value) : 0;
					// REPEAT wrapper: the same step list, re-run as a loop body. Its `until` is measured against the
					// state when the LOOP started, so "consume 20 rope" counts across every lap, not per lap.
					if (rb.Contains("\"repeat\":{"))
					{
						var rc = System.Text.RegularExpressions.Regex.Match(rb, "\"consumed\":\\{\"item\":(\\d+),\"n\":(\\d+)\\}");
						var rt = System.Text.RegularExpressions.Regex.Match(rb, "\"repeat\":\\{\"until\":\\{\"times\":(\\d+)");
						var rm = System.Text.RegularExpressions.Regex.Match(rb, "\"moved\":\\{([^}]*)\\}");
						var mx = System.Text.RegularExpressions.Regex.Match(rb, "\"max\":(\\d+)");
						string kind = ""; int n = 0, it = -1, dx = 0, dy = 0;
						if (rc.Success) { kind = "consumed"; it = int.Parse(rc.Groups[1].Value); n = int.Parse(rc.Groups[2].Value); }
						else if (rt.Success) { kind = "times"; n = int.Parse(rt.Groups[1].Value); }
						else if (rm.Success)
						{
							kind = "moved";
							var g = rm.Groups[1].Value;
							var gx = System.Text.RegularExpressions.Regex.Match(g, "\"dx\":(-?\\d+)");
							var gy = System.Text.RegularExpressions.Regex.Match(g, "\"dy\":(-?\\d+)");
							dx = gx.Success ? int.Parse(gx.Groups[1].Value) : 0;
							dy = gy.Success ? int.Parse(gy.Groups[1].Value) : 0;
						}
						ActExecutor.StartRepeat(steps, tf, kind, n, it, dx, dy,
							mx.Success ? int.Parse(mx.Groups[1].Value) : 0);
						body = "{\"ok\":true,\"repeat\":true,\"body_steps\":" + steps.Count + "}";
					}
					else
					{
						ActExecutor.Start(steps, tf);
						body = "{\"ok\":true,\"steps\":" + steps.Count + "}";
					}
				}
			}
			// /place_at — 语义放置:只说放什么、放哪。物品按名字(不是槽号),坐标相对原点格。
			// 完成判据不归调用方管:砖在那儿了就是放好了。
			else if (path == "/place_at")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace("\n", "").Replace("\r", "").Replace("\t", "");
				var itM = System.Text.RegularExpressions.Regex.Match(rb, "\"item\"\\s*:\\s*\"([^\"]*)\"");
				var atM = System.Text.RegularExpressions.Regex.Match(rb, "\"at\"\\s*:\\s*\\[\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*\\]");
				// "world" takes the cell outright, so a caller holding a known coordinate never has to convert it
				// into an offset from a moving origin.
				var wM = System.Text.RegularExpressions.Regex.Match(rb, "\"world\"\\s*:\\s*\\[\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*\\]");
				var nM = System.Text.RegularExpressions.Regex.Match(rb, "\"n\"\\s*:\\s*(\\d+)");
				var stM = System.Text.RegularExpressions.Regex.Match(rb, "\"step\"\\s*:\\s*\\[\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*\\]");
				var posM = wM.Success ? wM : atM;
				if (!itM.Success || !posM.Success)
				{
					body = "{\"accepted\":false,\"reason\":\"bad_params\",\"usage\":\"POST /place_at {\\\"item\\\":\\\"绳\\\",\\\"at\\\":[0,-1]} 或 {\\\"world\\\":[2051,239]}; 可加 n/step\"}";
					status = 400;
				}
				else
				{
					int n = nM.Success ? int.Parse(nM.Groups[1].Value) : 1;
					int sdx = stM.Success ? int.Parse(stM.Groups[1].Value) : 0;
					int sdy = stM.Success ? int.Parse(stM.Groups[2].Value) : 0;
					bool ok = PlaceAction.Start(itM.Groups[1].Value,
						int.Parse(posM.Groups[1].Value), int.Parse(posM.Groups[2].Value), n, sdx, sdy, wM.Success, out string why);
					// "accepted", not "ok": this says the request was taken, NOT that anything got built. Only
					// /place_at_status can say that, and it says it per cell.
					body = ok ? "{\"accepted\":true,\"n\":" + n + ",\"note\":\"poll /place_at_status for what actually happened\"}"
							  : "{\"accepted\":false,\"reason\":\"" + JsonEsc(why) + "\"}";
				}
			}
			// /rope_ladder — build a rope column N tall from where the player stands. Place as far as the arm reaches,
			// climb the rope just placed, repeat. Both phases end on a world fact, so it holds up at any move speed.
			else if (path == "/rope_ladder")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace("\n", "").Replace("\r", "").Replace("\t", "");
				var itM = System.Text.RegularExpressions.Regex.Match(rb, "\"item\"\\s*:\\s*\"([^\"]*)\"");
				var nM = System.Text.RegularExpressions.Regex.Match(rb, "\"n\"\\s*:\\s*(\\d+)");
				string item = itM.Success ? itM.Groups[1].Value : "绳";
				int n = nM.Success ? int.Parse(nM.Groups[1].Value) : 20;
				bool ok = RopeLadder.Start(item, n, out string why);
				body = ok ? "{\"accepted\":true,\"item\":\"" + JsonEsc(item) + "\",\"n\":" + n + ",\"note\":\"poll /rope_ladder_status for what actually happened\"}"
						  : "{\"accepted\":false,\"reason\":\"" + JsonEsc(why) + "\"}";
			}
			// /bridge — lay a platform run N long. Place as far as the arm reaches, walk out onto what was laid,
			// repeat. Walking happens only on ground already placed, so no speed matching is involved.
			else if (path == "/bridge")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace("\n", "").Replace("\r", "").Replace("\t", "");
				var itM = System.Text.RegularExpressions.Regex.Match(rb, "\"item\"\\s*:\\s*\"([^\"]*)\"");
				var dM = System.Text.RegularExpressions.Regex.Match(rb, "\"dir\"\\s*:\\s*\"(left|right)\"");
				var nM = System.Text.RegularExpressions.Regex.Match(rb, "\"n\"\\s*:\\s*(\\d+)");
				string item = itM.Success ? itM.Groups[1].Value : "木平台";
				string dir = dM.Success ? dM.Groups[1].Value : "right";
				int n = nM.Success ? int.Parse(nM.Groups[1].Value) : 10;
				bool ok = BridgeBuilder.Start(item, dir, n, out string why);
				body = ok ? "{\"accepted\":true,\"item\":\"" + JsonEsc(item) + "\",\"dir\":\"" + dir + "\",\"n\":" + n + ",\"note\":\"poll /bridge_status for what actually happened\"}"
						  : "{\"accepted\":false,\"reason\":\"" + JsonEsc(why) + "\"}";
			}
			// /pillar — build a solid column N tall by jump-placing blocks under the feet.
			else if (path == "/pillar")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace("\n", "").Replace("\r", "").Replace("\t", "");
				var itM = System.Text.RegularExpressions.Regex.Match(rb, "\"item\"\\s*:\\s*\"([^\"]*)\"");
				var nM = System.Text.RegularExpressions.Regex.Match(rb, "\"n\"\\s*:\\s*(\\d+)");
				string item = itM.Success ? itM.Groups[1].Value : "木材";
				int n = nM.Success ? int.Parse(nM.Groups[1].Value) : 9;
				var colM = System.Text.RegularExpressions.Regex.Match(rb, "\"col\"\\s*:\\s*(-?\\d+)");
				int col = colM.Success ? int.Parse(colM.Groups[1].Value) : -1;
				bool ok = PillarUp.Start(item, n, col, out string why);
				body = ok ? "{\"accepted\":true,\"item\":\"" + JsonEsc(item) + "\",\"n\":" + n + ",\"note\":\"poll /pillar_status for what actually happened\"}"
						  : "{\"accepted\":false,\"reason\":\"" + JsonEsc(why) + "\"}";
			}
			else if (path == "/pillar_status")
			{
				body = PillarUp.StatusJson();
			}
			else if (path == "/pillar_stop")
			{
				PillarUp.Stop();
				body = "{\"ok\":true}";
			}
			// /place_walls — place background walls at an ORDERED list of cells (order matters for vanilla spread).
			// body: {"item":"木墙","cells":[[x,y],[x,y],...]}  cells are absolute, placed strictly in the given order.
			else if (path == "/place_walls")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace("\n", "").Replace("\r", "").Replace("\t", "");
				var itM = System.Text.RegularExpressions.Regex.Match(rb, "\"item\"\\s*:\\s*\"([^\"]*)\"");
				var cells = new System.Collections.Generic.List<(int, int)>();
				foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(rb,
					"\\[\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*\\]"))
					cells.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
				if (!itM.Success || cells.Count == 0)
				{
					body = "{\"accepted\":false,\"reason\":\"bad_params\",\"usage\":\"POST /place_walls {item, cells:[[x,y],...]}\"}";
					status = 400;
				}
				else
				{
					bool ok = PlaceWalls.Start(itM.Groups[1].Value, cells, out string why);
					body = ok ? "{\"accepted\":true,\"cells\":" + cells.Count + ",\"note\":\"poll /place_walls_status\"}"
							  : "{\"accepted\":false,\"reason\":\"" + JsonEsc(why) + "\"}";
				}
			}
			else if (path == "/place_walls_status")
			{
				body = PlaceWalls.StatusJson();
			}
			else if (path == "/place_walls_stop")
			{
				PlaceWalls.Stop();
				body = "{\"ok\":true}";
			}
			// /walk_place — walk to dest_x, placing furniture at listed targets whenever they come within reach.
			// body: {"dest_x":N,"targets":[{"x":N,"y":N,"item":"木桌"}, ...]}
			else if (path == "/walk_place")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace("\n", "").Replace("\r", "").Replace("\t", "");
				var dM = System.Text.RegularExpressions.Regex.Match(rb, "\"dest_x\"\\s*:\\s*(-?\\d+)");
				var targets = new System.Collections.Generic.List<(int, int, string)>();
				foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(rb,
					"\\{\\s*\"x\"\\s*:\\s*(-?\\d+)\\s*,\\s*\"y\"\\s*:\\s*(-?\\d+)\\s*,\\s*\"item\"\\s*:\\s*\"([^\"]*)\"\\s*\\}"))
					targets.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Value));
				if (!dM.Success || targets.Count == 0)
				{
					body = "{\"accepted\":false,\"reason\":\"bad_params\",\"usage\":\"POST /walk_place {dest_x, targets:[{x,y,item}]}\"}";
					status = 400;
				}
				else
				{
					bool ok = WalkPlace.Start(int.Parse(dM.Groups[1].Value), targets, out string why);
					body = ok ? "{\"accepted\":true,\"targets\":" + targets.Count + ",\"note\":\"poll /walk_place_status\"}"
							  : "{\"accepted\":false,\"reason\":\"" + JsonEsc(why) + "\"}";
				}
			}
			else if (path == "/walk_place_status")
			{
				body = WalkPlace.StatusJson();
			}
			else if (path == "/walk_place_stop")
			{
				WalkPlace.Stop();
				body = "{\"ok\":true}";
			}
			// /drop — fall through the platform underfoot down to solid ground (come off the roof onto the base).
			else if (path == "/drop")
			{
				bool ok = DropDown.Start(out string why);
				body = ok ? "{\"accepted\":true,\"note\":\"poll /drop_status\"}"
						  : "{\"accepted\":false,\"reason\":\"" + JsonEsc(why) + "\"}";
			}
			else if (path == "/drop_status")
			{
				body = DropDown.StatusJson();
			}
			else if (path == "/drop_stop")
			{
				DropDown.Stop();
				body = "{\"ok\":true}";
			}
			// /settle — brake to a full stop standing on a given column (no overshoot off a narrow platform).
			else if (path == "/settle")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var cM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"col\"\\s*:\\s*(-?\\d+)");
				if (!cM.Success) { body = "{\"accepted\":false,\"reason\":\"bad_params\",\"usage\":\"POST /settle {\\\"col\\\":2027}\"}"; status = 400; }
				else
				{
					bool ok = SettleAt.Start(int.Parse(cM.Groups[1].Value), out string why);
					body = ok ? "{\"accepted\":true,\"note\":\"poll /settle_status\"}"
							  : "{\"accepted\":false,\"reason\":\"" + JsonEsc(why) + "\"}";
				}
			}
			else if (path == "/settle_status")
			{
				body = SettleAt.StatusJson();
			}
			else if (path == "/settle_stop")
			{
				SettleAt.Stop();
				body = "{\"ok\":true}";
			}
			// /hop_up — jump until standing on the given surface row (getting off a rope onto a platform above it).
			else if (path == "/hop_up")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"row\"\\s*:\\s*(-?\\d+)");
				var cM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"col\"\\s*:\\s*(-?\\d+)");
				if (!rM.Success) { body = "{\"accepted\":false,\"reason\":\"bad_params\",\"usage\":\"POST /hop_up {\\\"row\\\":242,\\\"col\\\":2027}\"}"; status = 400; }
				else
				{
					int col = cM.Success ? int.Parse(cM.Groups[1].Value) : int.MinValue;
					bool ok = HopUp.Start(int.Parse(rM.Groups[1].Value), col, out string why);
					body = ok ? "{\"accepted\":true,\"note\":\"poll /hop_up_status\"}"
							  : "{\"accepted\":false,\"reason\":\"" + JsonEsc(why) + "\"}";
				}
			}
			else if (path == "/hop_up_status")
			{
				body = HopUp.StatusJson();
			}
			else if (path == "/bridge_status")
			{
				body = BridgeBuilder.StatusJson();
			}
			else if (path == "/bridge_stop")
			{
				BridgeBuilder.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/rope_ladder_status")
			{
				body = RopeLadder.StatusJson();
			}
			else if (path == "/rope_ladder_stop")
			{
				RopeLadder.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/place_at_status")
			{
				body = PlaceAction.StatusJson();
			}
			else if (path == "/place_at_stop")
			{
				PlaceAction.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/act_status")
			{
				body = ActExecutor.StatusJson();
			}
			else if (path == "/act_stop")
			{
				ActExecutor.Stop();
				body = "{\"ok\":true}";
			}
			// /origin — 所有相对坐标的锚点。身体跨 2~3 列,哪一列算数是条规则不是猜 ——
			// 所以让调用方在下偏移之前先看得见自己站在哪格,屏幕上也高亮。
			else if (path == "/origin")
			{
				var op = Main.LocalPlayer;
				int ocx = ActExecutor.OriginCx(op), ocy = ActExecutor.OriginCy(op);
				PathVisSystem.SetTiles(new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>
					{ (ocx, ocy, new Microsoft.Xna.Framework.Color(0, 220, 255, 200)) }, ttlFrames: 300);
				body = "{\"cx\":" + ocx + ",\"cy\":" + ocy
					+ ",\"pos\":[" + op.position.X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
					+ "," + op.position.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "]}";
			}
			// ---- LLM agent bridge (see AgentChat.cs) ----
			else if (path == "/nav_recede")
			{
				// Bellman receding-horizon nav (same engine as the K keybind). Poll /nav_recede_done for the outcome.
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var rb = reqBody.Replace(" ", "");
				var gxM = System.Text.RegularExpressions.Regex.Match(rb, "\"gx\":(-?\\d+)");
				var gyM = System.Text.RegularExpressions.Regex.Match(rb, "\"gy\":(-?\\d+)");
				if (!gxM.Success || !gyM.Success) { body = "{\"ok\":false,\"reason\":\"bad_request\"}"; status = 400; }
				else
				{
					// stand=true:目标是悬空格,要求真站上去(盖房)。不 snap 到地面,近处交给 A*。
					bool exact = System.Text.RegularExpressions.Regex.IsMatch(rb, "\"exact\":true");
					bool stand = System.Text.RegularExpressions.Regex.IsMatch(rb, "\"stand\":true");
					// reach=true:开箱子这种,够得着就行,不用站到那一格上。
					bool reach = System.Text.RegularExpressions.Regex.IsMatch(rb, "\"reach\":true");
					var nmode = stand ? RecedingNav.Mode.Stand : reach ? RecedingNav.Mode.Reach
						: exact ? RecedingNav.Mode.Mine : RecedingNav.Mode.Snap;
					RecedingNav.Start(int.Parse(gxM.Groups[1].Value), int.Parse(gyM.Groups[1].Value), nmode);
					body = "{\"ok\":true}";
				}
			}
			else if (path == "/nav_recede_done")
			{
				if (RecedingNav.Active)
					body = "{\"done\":false,\"status\":\"running\"}";
				else if (RecedingNav.LastStop == "done")
					body = "{\"done\":true,\"status\":\"done\"}";
				else
					body = "{\"done\":false,\"status\":\"failed\",\"reason\":\"" + (RecedingNav.LastStop ?? "never_ran") + "\"}";
			}
			else if (path == "/nav_recede_stop")
			{
				RecedingNav.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/instruction")
			{
				body = AgentChat.Instructions.TryDequeue(out var ins)
					? "{\"instruction\":\"" + JsonEsc(ins) + "\"}"
					: "{\"instruction\":null}";
			}
			else if (path == "/say")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var tM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"text\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
				if (tM.Success)
				{
					// {"bot":true} = 脚本播报,灰蓝;缺省 = LLM 说的,橙
					bool isBot = System.Text.RegularExpressions.Regex.IsMatch(reqBody, "\"bot\"\\s*:\\s*true");
					AgentChat.Say(JsonUnesc(tM.Groups[1].Value), isBot);
					body = "{\"ok\":true}";
				}
				else { body = "{\"error\":\"bad_params\",\"usage\":\"POST /say {\\\"text\\\":\\\"...\\\"}\"}"; status = 400; }
			}
			else if (path == "/item_info")
			{
				// POST {"slot":18} or {"name":"Bomb"} → full item info so the agent can tell same-named / confusable
				// items apart (BOMB summons a fairy; 炸弹 destroys tiles). Returns type flags + tooltip lines.
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var slM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"slot\"\\s*:\\s*(\\d+)");
				var inM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"name\"\\s*:\\s*\"([^\"]+)\"");
				var pl = Main.LocalPlayer;
				Item it = null;
				if (pl != null && slM.Success)
				{
					int sl = int.Parse(slM.Groups[1].Value);
					if (sl >= 0 && sl < pl.inventory.Length) it = pl.inventory[sl];
				}
				else if (pl != null && inM.Success)
				{
					string want = inM.Groups[1].Value.ToLowerInvariant();
					foreach (var cand in pl.inventory)
						if (cand != null && !cand.IsAir && (cand.Name ?? "").ToLowerInvariant() == want) { it = cand; break; }
				}
				if (it == null || it.IsAir) { body = "{\"found\":false}"; }
				else
				{
					var isb = new StringBuilder("{\"found\":true");
					isb.Append(",\"name\":\"").Append(JsonEsc(it.Name ?? "")).Append('"');
					isb.Append(",\"type\":").Append(it.type);
					isb.Append(",\"stack\":").Append(it.stack);
					isb.Append(",\"damage\":").Append(it.damage);
					isb.Append(",\"pick\":").Append(it.pick);
					isb.Append(",\"axe\":").Append(it.axe);
					isb.Append(",\"hammer\":").Append(it.hammer);
					isb.Append(",\"createTile\":").Append(it.createTile);
					isb.Append(",\"createWall\":").Append(it.createWall);
					isb.Append(",\"consumable\":").Append(it.consumable ? "true" : "false");
					isb.Append(",\"healLife\":").Append(it.healLife);
					isb.Append(",\"healMana\":").Append(it.healMana);
					isb.Append(",\"useStyle\":").Append(it.useStyle);
					isb.Append(",\"tooltip\":\"").Append(JsonEsc(ItemTooltipText(it))).Append('"');
					isb.Append('}');
					body = isb.ToString();
				}
			}
			else if (path == "/find_biome")
			{
				// POST {"name":"jungle"} → 那个生态一个能站的坐标。整张图都读得到,所以找生态是查询不是探索:
				// 扫标志方块、取平均、往下贴到地面。支持 jungle/snow/desert/dungeon/corruption/crimson/hallow。
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var bnM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"name\"\\s*:\\s*\"([a-zA-Z_]+)\"");
				string biome = bnM.Success ? bnM.Groups[1].Value.ToLowerInvariant() : "";
				ushort[] sigTypes = BiomeSig(biome);
				if (sigTypes == null) { body = "{\"error\":\"unknown_biome\",\"name\":\"" + JsonEsc(biome) + "\"}"; status = 400; }
				else
				{
					var want = new System.Collections.Generic.HashSet<ushort>(sigTypes);
					// 不取质心:标志方块从地表铺到洞穴层,平均下来是个没人会挖过去的地下点。
					// 要的是【地表入口】——头顶露天、离人最近的那块,这样 nav 是走过去而不是往下打洞。
					var pl0 = Main.LocalPlayer;
					int px = pl0 != null ? (int)(pl0.Center.X / 16f) : Main.maxTilesX / 2;
					int total = 0, bestX = -1, bestY = -1, bestDx = int.MaxValue;
					int surfaceCap = (int)Main.worldSurface;   // signature tiles above this are surface; below is underground
					for (int x = 0; x < Main.maxTilesX; x += 2)
						for (int y = 0; y < surfaceCap && y < Main.maxTilesY; y += 2)
						{
							var t = Main.tile[x, y];
							if (!t.HasTile || !want.Contains(t.TileType)) continue;
							total++;
							bool sky = true;
							for (int k = 1; k <= 5 && sky; k++)
							{
								if (y - k < 0) break;
								var a = Main.tile[x, y - k];
								if (a.HasTile && Main.tileSolid[a.TileType]) sky = false;
							}
							if (!sky) continue;
							int dx = System.Math.Abs(x - px);
							if (dx < bestDx) { bestDx = dx; bestX = x; bestY = y; }
						}
					if (bestX < 0) { body = "{\"found\":false,\"count\":" + total + "}"; }
					else
					{
						// stand ON the surface tile: entrance point is the open cell just above it.
						body = "{\"found\":true,\"x\":" + bestX + ",\"y\":" + (bestY - 1) + ",\"count\":" + total + "}";
					}
				}
			}
			else if (path == "/find_descent")
			{
				// POST {"name":"jungle"} → 下地狱【真实路线】最便宜的那个地表入口。下降代价是拓扑的
				// (S 形洞从顶上进比哪儿直挖都快),所以从整条地狱带往上 flood,取露天候选里 H 最小的。
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var bnM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"name\"\\s*:\\s*\"([a-zA-Z_]+)\"");
				string biome = bnM.Success ? bnM.Groups[1].Value.ToLowerInvariant() : "";
				ushort[] sigTypes = BiomeSig(biome);
				if (sigTypes == null) { body = "{\"error\":\"unknown_biome\",\"name\":\"" + JsonEsc(biome) + "\"}"; status = 400; }
				else
				{
					var dd = ComputeDescent(sigTypes, out string why);
					body = dd == null
						? "{\"found\":false,\"reason\":\"" + JsonEsc(why) + "\"}"
						: "{\"found\":true,\"x\":" + dd.EntX + ",\"y\":" + dd.EntY + ",\"cost\":" + dd.Cost + ",\"entrances\":" + dd.Cands + "}";
				}
			}
			else if (path == "/hell_line")
			{
				// POST {"x":起点列} → 地狱 170 格桥线。方向按玩家在哪半边定,房子在近端。
				// 只算只画不搭 —— 线对不对先用眼睛验。
				string rb = ReadBody(ctx).Replace(" ", "");
				var hxm = System.Text.RegularExpressions.Regex.Match(rb, "\"x\"\\s*:\\s*(-?\\d+)");
				var pl = Main.LocalPlayer;
				int bx = hxm.Success ? int.Parse(hxm.Groups[1].Value)
					: (pl != null ? ActExecutor.OriginCx(pl) : Main.maxTilesX / 2);
				int hdir = bx < Main.maxTilesX / 2 ? 1 : -1;
				var hr = HellLine.Compute(bx, hdir);
				if (!hr.Found) body = "{\"found\":false,\"reason\":\"" + JsonEsc(hr.Why) + "\"}";
				else
				{
					var hv = new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>();
					var lc = new Microsoft.Xna.Framework.Color(0, 200, 255, 140);
					var hc = new Microsoft.Xna.Framework.Color(255, 180, 0, 230);
					foreach (var (lx, ly) in hr.Line) hv.Add((lx, ly, lc));
					for (int k = 0; k < HouseBuilder.RoomWidth + 1; k++)
						hv.Add((hr.HouseX + hdir * k, hr.HouseY, hc));
					PathVisSystem.SetTiles(hv, 7200);
					var hsb = new StringBuilder();
					hsb.Append("{\"found\":true,\"dir\":").Append(hdir)
					   .Append(",\"start\":[").Append(hr.StartX).Append(',').Append(hr.StartY)
					   .Append("],\"house\":[").Append(hr.HouseX).Append(',').Append(hr.HouseY)
					   .Append("],\"dig_cells\":").Append(hr.DigCells).Append(",\"cost\":").Append(hr.Cost)
					   .Append(",\"line\":[");
					for (int i = 0; i < hr.Line.Count; i++)
					{
						if (i > 0) hsb.Append(',');
						hsb.Append('[').Append(hr.Line[i].x).Append(',').Append(hr.Line[i].y).Append(']');
					}
					hsb.Append("]}");
					body = hsb.ToString();
					DiagLog.Write($"[hell-line] start=({hr.StartX},{hr.StartY}) dir={hdir} house=({hr.HouseX},{hr.HouseY}) dig={hr.DigCells} cost={hr.Cost}");
				}
			}
			else if (path == "/descent_route")
			{
				// POST {"name":"jungle"} → /find_descent 的入口 + 描出来的下地狱路线 + 走廊内的宝藏(箱子/生命水晶)。
				// 顺便在世界里画两分钟:青色主线,金箱黄叉,水晶粉叉。line_x/line_y 是从主线拐出去的接驳点。
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var bnM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"name\"\\s*:\\s*\"([a-zA-Z_]+)\"");
				string biome = bnM.Success ? bnM.Groups[1].Value.ToLowerInvariant() : "";
				ushort[] sigTypes = BiomeSig(biome);
				if (sigTypes == null) { body = "{\"error\":\"unknown_biome\",\"name\":\"" + JsonEsc(biome) + "\"}"; status = 400; }
				else
				{
					var dd = ComputeDescent(sigTypes, out string why);
					if (dd == null) body = "{\"found\":false,\"reason\":\"" + JsonEsc(why) + "\"}";
					else
					{
						_descentField = dd.Field;   // kept for /descent_h progress queries
						// trace the line: strictly-descending greedy on H (Dijkstra guarantees every non-source cell
						// has a lower-H neighbour, so this terminates at a hell source; coarse but always a real route)
						var line = new System.Collections.Generic.List<(int x, int y)>();
						// 先接一段「玩家现在的位置 → 入口」。脚手架原来从入口起,而搜索盒子是脚手架的包围盒,
						// 所以人在出生点跑"去地狱"时,出生点到丛林入口这一路完全在盒子外——那一路的箱子一个都看不见。
						{
							var pl = Main.LocalPlayer;
							if (pl != null)
							{
								int pcx = ActExecutor.OriginCx(pl), pcy = ActExecutor.OriginCy(pl);
								if (System.Math.Abs(pcx - dd.EntX) + System.Math.Abs(pcy - dd.EntY) > 4)
								{
									var af = MazeWand.BuildField(dd.EntX, dd.EntY, pcx, pcy);
									var ac = (x: pcx, y: pcy);
									var aseen = new System.Collections.Generic.HashSet<(int, int)>();
									for (int step = 0; step < 20000; step++)
									{
										line.Add(ac);
										if (!aseen.Add(ac)) break;
										if (ac.x == dd.EntX && ac.y == dd.EntY) break;
										if (!af.TryGetValue((ac.x, ac.y), out int ah)) break;
										int bn = ah; var bc = ac; bool moved = false;
										foreach (var (dx2, dy2) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
										{
											var n = (x: ac.x + dx2, y: ac.y + dy2);
											if (af.TryGetValue((n.x, n.y), out int nh) && nh < bn) { bn = nh; bc = n; moved = true; }
										}
										if (!moved) break;
										ac = bc;
									}
								}
							}
						}
						// 记下地表那段有多长:预算只该按【入口往下】那段算。人在出生点时前半段能有上千格,
						// 算进去等于凭空多发一倍额度,而那些额度全花在地表 —— 空岛就是这么去的。
						int surfaceLen = line.Count;
						var cur = (dd.EntX, dd.EntY);
						var seen = new System.Collections.Generic.HashSet<(int, int)>();
						for (int step = 0; step < 20000; step++)
						{
							line.Add(cur);
							if (!seen.Add(cur)) break;
							if (dd.Field.TryGetValue(cur, out int hc) && hc == 0) break;
							int bestN = int.MaxValue; var best = cur;
							foreach (var (dx2, dy2) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
							{
								var n = (cur.Item1 + dx2, cur.Item2 + dy2);
								if (!dd.Field.TryGetValue(n, out int dn)) continue;
								if (dn < bestN) { bestN = dn; best = n; }
							}
							if (best == cur) break;
							cur = best;
						}
						// 走廊按【绕道代价】划,不按直线距离:走一格 3、挖一格 26,"离 25 格"说明不了值不值。
						// 第二张多源场种在主线上(线上每格 0),从宝藏往下降就描出真实绕道路径,沿途 StepCost 就是价钱。
						var digM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"dig_max\"\\s*:\\s*(\\d+)");
						var wlkM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"walk_max\"\\s*:\\s*(\\d+)");
						var digM2 = System.Text.RegularExpressions.Regex.Match(reqBody, "\"dig_max2\"\\s*:\\s*(\\d+)");
						var wlkM2 = System.Text.RegularExpressions.Regex.Match(reqBody, "\"walk_max2\"\\s*:\\s*(\\d+)");
						int digMax = digM.Success ? int.Parse(digM.Groups[1].Value) : 20;
						int walkMax = wlkM.Success ? int.Parse(wlkM.Groups[1].Value) : 60;
						int digMax2 = digM2.Success ? int.Parse(digM2.Groups[1].Value) : 50;
						int walkMax2 = wlkM2.Success ? int.Parse(wlkM2.Groups[1].Value) : 120;
						int rejectBound = digMax2 * 160 + walkMax2 * 12;   // loose upper bound for the quick reject
						const int margin = 80;
						int bMinX = int.MaxValue, bMaxX = int.MinValue, bMinY = int.MaxValue, bMaxY = int.MinValue;
						foreach (var (lx, ly) in line)
						{
							if (lx < bMinX) bMinX = lx;
							if (lx > bMaxX) bMaxX = lx;
							if (ly < bMinY) bMinY = ly;
							if (ly > bMaxY) bMaxY = ly;
						}
						bMinX = System.Math.Max(0, bMinX - margin); bMaxX = System.Math.Min(Main.maxTilesX - 1, bMaxX + margin);
						bMinY = System.Math.Max(0, bMinY - margin); bMaxY = System.Math.Min(Main.maxTilesY - 1, bMaxY + margin);
						var lineField = MazeWand.BuildFieldMulti(line, bMinX, bMaxX, bMinY, bMaxY);
						// junction cell → its index along the line, so the executor can visit treasures in line order
						var lineIdx = new System.Collections.Generic.Dictionary<(int, int), int>();
						for (int i = 0; i < line.Count; i++)
							if (!lineIdx.ContainsKey(line[i])) lineIdx[line[i]] = i;
						int rejOff = 0, rejFar = 0, rejNoLink = 0;
						var rejLog = new System.Text.StringBuilder();
						var treasures = new System.Collections.Generic.List<(int x, int y, string kind, int jx, int jy, int li, int dig, int walk, string tier, System.Collections.Generic.List<(int, int)> path)>();
						for (int x = bMinX; x <= bMaxX; x++)
							for (int y = bMinY; y <= bMaxY; y++)
							{
								var t = Main.tile[x, y];
								if (!t.HasTile) continue;
								string kind = null;
								// 箱子种类看 TileFrameX/36 这个 style。style 0 = 木箱(最常见、基本没好东西),
								// 单独标出来是为了给它更低的价值;红木/乌木/珍珠木各自独立 style,不会混进来。
								if ((t.TileType == Terraria.ID.TileID.Containers || t.TileType == Terraria.ID.TileID.Containers2)
									&& t.TileFrameX % 36 == 0 && t.TileFrameY % 36 == 0)
								{
									// 上锁的箱子(地狱暗影箱要暗影钥匙、地牢金箱要金钥匙)开不了 —— 没钥匙就别当目标,
									// 不然绕一大段路过去开不开,还白占一次收集额度。用 vanilla 自己的判据。
									if (Terraria.Chest.IsLocked(x, y)) continue;
									// 丛林蜥蜴箱(style 16)在神庙里,外面那圈砖 Picksaw 之前挖不动,进不去,别当目标。
									if (t.TileType == Terraria.ID.TileID.Containers && t.TileFrameX / 36 == 16) continue;
									// 蜂巢里的不捡:蜂巢块难挖,进去容易被蜂群围,出来那一段一直卡。
									// 判据用【墙】不用方块:宝藏就贴在蜂巢墙上,查方块会漏。
									if (InHive(x, y)) continue;
									kind = (t.TileType == Terraria.ID.TileID.Containers && t.TileFrameX / 36 == 0) ? "wood_chest" : "chest";
								}
								else if (t.TileType == Terraria.ID.TileID.Heart
									&& t.TileFrameX % 36 == 0 && t.TileFrameY % 36 == 0)
								{
									if (InHive(x, y)) continue;
									kind = "heart";
								}
								if (kind == null) continue;
									// 每一次淘汰都留下坐标和原因:(3667,655) 那两颗水晶人擦肩而过却没进池子,
									// 而三条 continue 一条日志都没有,根本看不出是哪条把它扔了。
									if (!lineField.TryGetValue((x, y), out int d0)) { rejOff++; rejLog.Append($" {kind[0]}({x},{y})场外"); continue; }
									if (d0 > rejectBound) { rejFar++; rejLog.Append($" {kind[0]}({x},{y})远{d0}"); continue; }
								// trace the detour path treasure→line by descending the line-field
								var bpath = new System.Collections.Generic.List<(int, int)>();
								var bc2 = (x, y);
								var bseen = new System.Collections.Generic.HashSet<(int, int)>();
								while (bpath.Count < 4000)
								{
									bpath.Add(bc2);
									if (!bseen.Add(bc2)) break;
									if (lineField.TryGetValue(bc2, out int bh) && bh == 0) break;
									int bestN = int.MaxValue; var nb = bc2;
									foreach (var (dx3, dy3) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
									{
										var n = (bc2.Item1 + dx3, bc2.Item2 + dy3);
										if (lineField.TryGetValue(n, out int dn) && dn < bestN) { bestN = dn; nb = n; }
									}
									if (nb == bc2) break;
									bc2 = nb;
								}
								var junction = bpath[bpath.Count - 1];
									if (!lineField.TryGetValue(junction, out int endH) || endH != 0)
										{ rejNoLink++; rejLog.Append($" {kind[0]}({x},{y})接不上线"); continue; }
								// 按 StepCost 分类:超过最贵的移动就是挖。回程走已挖开的隧道,
								// 所以单向计数才是该设上限的那个单位。
								int nDig = 0, nWalk = 0;
								for (int i = 1; i < bpath.Count; i++)
								{
									int c = MazeWand.StepCostPublic(bpath[i - 1].Item1, bpath[i - 1].Item2, bpath[i].Item1, bpath[i].Item2);
									if (c > MazeWand.MaxMoveCost) nDig++; else nWalk++;
								}
								// 不在这儿判要不要:硬阈值会切出 199 进、201 不进的悬崖,而且这个判决在出发前就冻结了 ——
								// 走到 199 那儿之后 201 明明变近了也回不来。这里只收进候选池,取舍留给下面按性价比排。
								string tier = nDig <= digMax && nWalk <= walkMax ? "main" : "optional";
								treasures.Add((x, y, kind, junction.Item1, junction.Item2,
									lineIdx.TryGetValue(junction, out int li0) ? li0 : 0, nDig, nWalk, tier, bpath));
							}
						DiagLog.Write($"[route] 扫描:入池{treasures.Count} 淘汰 场外{rejOff}/太远{rejFar}/接不上线{rejNoLink} —{rejLog}");
						// 定向越野:点=宝藏,边=两点代价,预算内求总价值最大。单向下降 → DAG → DP 出精确最优。
						// 两点距离用算术估(零次额外 flood):扎堆的挂点相近,中间那段≈0,一窝只付一次进出路费。
						var chain = new System.Collections.Generic.List<int>();       // visit order, indices into `treasures`
						{
							var cand = new System.Collections.Generic.List<int>();
							for (int i = 0; i < treasures.Count; i++) cand.Add(i);
							cand.Sort((a, b) => treasures[a].li.CompareTo(treasures[b].li));   // 线序 = 深度序,单向下降
							int n = cand.Count;
							// det = 单程离线代价(挖过的隧道回程免费,所以挖只算一次)。往返在 Extra 里配对收。
							var det = new int[n];
							var val = new int[n];
							for (int i = 0; i < n; i++)
							{
								var tr = treasures[cand[i]];
								det[i] = tr.walk + tr.dig * DigWalkRatio;   // 单程:出去。回来那程在 Extra 里按需收
								val[i] = TreasureValue(tr.kind);
							}
							// 预算只管【绕路】那部分:主线本来就要走,不该占额度。
							// 只按【入口→地狱】那段发预算,地表那段不算 —— 它不是下丛林的路,不该撑起绕道额度。
							int budget = (int)((line.Count - surfaceLen) * DetourBudgetFrac);
							int step = System.Math.Max(1, budget / BudgetSteps);
							int B = budget / step + 2;
							// i→j 只收【从 i 回线】+【从线进 j】两个单程,回程按 gap 封顶所以一窝宝藏共享进出。
							// 原来收 det[i]/2+det[j] 而 det 本身是往返,中间站的回程被收两遍,九个就吃光预算。
							int Extra(int i, int j)
							{
								int gap = System.Math.Abs(treasures[cand[j]].li - treasures[cand[i]].li);
								return System.Math.Min(det[i], gap) + det[j];
							}
							var f = new int[n, B];
							var from = new int[n, B];
							for (int i = 0; i < n; i++) for (int b = 0; b < B; b++) { f[i, b] = int.MinValue; from[i, b] = -2; }
							for (int i = 0; i < n; i++) { int c = det[i] / step; if (c < B && val[i] > f[i, c]) { f[i, c] = val[i]; from[i, c] = -1; } }
							for (int i = 0; i < n; i++)
								for (int b = 0; b < B; b++)
								{
									if (f[i, b] == int.MinValue) continue;
									for (int j = i + 1; j < n; j++)
									{
										int nb = b + Extra(i, j) / step;
										if (nb >= B) continue;
										int nv = f[i, b] + val[j];
										if (nv > f[j, nb]) { f[j, nb] = nv; from[j, nb] = i; }
									}
								}
							int bi = -1, bb = -1, best = 0;
							for (int i = 0; i < n; i++) for (int b = 0; b < B; b++) if (f[i, b] > best) { best = f[i, b]; bi = i; bb = b; }
							while (bi >= 0)
							{
								chain.Add(cand[bi]);
								int pi = from[bi, bb];
								if (pi < 0) break;
								bb -= Extra(pi, bi) / step;
								bi = pi;
							}
							chain.Reverse();
							// 选了谁、每个多贵,直接印出来:只报总数的话"为什么才 9 个"没法回答。
							{
								var sb2 = new System.Text.StringBuilder();
								int cheap = 0, tot = 0;
								for (int i = 0; i < n; i++) { tot += det[i]; if (det[i] <= budget / 10) cheap++; }
								foreach (int ci in chain) sb2.Append($" {treasures[ci].kind[0]}({treasures[ci].x},{treasures[ci].y})w{treasures[ci].walk}d{treasures[ci].dig}");
								DiagLog.Write($"[route] 候选均价{(n > 0 ? tot / n : 0)} 便宜的({budget / 10}以内){cheap}个 选中:{sb2}");
							}
							DiagLog.Write($"[route] DP 候选{n} 预算{budget}({B}档) 选中{chain.Count} 价值{best}");
						}
						var threaded = new System.Collections.Generic.List<(int, int)>();   // the single line, entrance→hell
						{
							var stops = new System.Collections.Generic.List<(int x, int y)>();
							foreach (int ti in chain) stops.Add((treasures[ti].x, treasures[ti].y));
							stops.Add((line[line.Count - 1].x, line[line.Count - 1].y));    // finish at hell
							// 从玩家现在站的地方起,不是从入口起——不然前半程(走去入口那段)不在路线里
							var pl0 = Main.LocalPlayer;
							var cursor = pl0 != null
								? (x: ActExecutor.OriginCx(pl0), y: ActExecutor.OriginCy(pl0))
								: (x: dd.EntX, y: dd.EntY);
							threaded.Add(cursor);
							var swAll = System.Diagnostics.Stopwatch.StartNew();
							int legN = 0, legFail = 0;
							foreach (var goal in stops)
							{
								legN++;
								var f = MazeWand.BuildField(goal.x, goal.y, cursor.x, cursor.y);
								if (!f.ContainsKey(cursor)) { legFail++; continue; }        // leg unroutable — skip to next
								// 从脚下的 H 起步只收严格更低的邻居:起点若是 int.MaxValue,局部窝里两格会互相挑来挑去,
								// 一轮加 2 格跑满 20000 步,每段都这样 /descent_route 90s 也回不来。seen 再兜住平地和环。
								var lseen = new System.Collections.Generic.HashSet<(int, int)> { (cursor.x, cursor.y) };
								for (int step = 0; step < 20000; step++)
								{
									if (cursor.x == goal.x && cursor.y == goal.y) break;
									if (!f.TryGetValue((cursor.x, cursor.y), out int hc0)) break;
									if (hc0 == 0) break;
									int bestN = hc0; var best = cursor;
									foreach (var (dx2, dy2) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
									{
										var n = (x: cursor.x + dx2, y: cursor.y + dy2);
										if (f.TryGetValue((n.x, n.y), out int dn) && dn < bestN) { bestN = dn; best = n; }
									}
									if (best.x == cursor.x && best.y == cursor.y) break;
									if (!lseen.Add((best.x, best.y))) break;
									cursor = best;
									threaded.Add((cursor.x, cursor.y));
								}
							}
							DiagLog.Write($"[descent-thread] legs={legN} unroutable={legFail} cells={threaded.Count} ms={swAll.ElapsedMilliseconds}");
						}

						// 只画穿宝线。所有 tier(main+optional)现在都在线上,所以一根分叉都不画。
						// 原来那条 hell-only 主线也不画——它已经不是路线,只是量代价的脚手架。
						var vis = new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>();
						var trunk = new Microsoft.Xna.Framework.Color(0, 200, 255, 140);
						foreach (var (lx, ly) in threaded) vis.Add((lx, ly, trunk));
						foreach (var tr in treasures)
						{
							bool opt = tr.tier == "optional";
							var mc = tr.kind == "chest"
								? new Microsoft.Xna.Framework.Color(255, 180, 0, opt ? 160 : 230)
								: new Microsoft.Xna.Framework.Color(255, 60, 120, opt ? 160 : 230);
							vis.Add((tr.x, tr.y, mc)); vis.Add((tr.x + 1, tr.y, mc));
							vis.Add((tr.x, tr.y + 1, mc)); vis.Add((tr.x + 1, tr.y + 1, mc));
						}
						PathVisSystem.SetTiles(vis, 7200);
						var rsb = new StringBuilder();
						var tail = threaded.Count > 0 ? threaded[threaded.Count - 1] : (line[line.Count - 1].x, line[line.Count - 1].y);
						rsb.Append("{\"found\":true,\"entrance\":{\"x\":").Append(dd.EntX).Append(",\"y\":").Append(dd.EntY)
						   .Append("},\"hell_x\":").Append(tail.Item1).Append(",\"hell_y\":").Append(tail.Item2)
						   .Append(",\"cost\":").Append(dd.Cost).Append(",\"line_len\":").Append(threaded.Count)
						   .Append(",\"scaffold_len\":").Append(line.Count)
						   .Append(",\"dig_max\":").Append(digMax).Append(",\"walk_max\":").Append(walkMax);
						// the threaded line's own cells, so its SHAPE can be inspected outside the game instead of
						// judged by eye through a tile overlay
						rsb.Append(",\"line\":[");
						for (int i = 0; i < threaded.Count; i++)
						{
							if (i > 0) rsb.Append(',');
							rsb.Append('[').Append(threaded[i].Item1).Append(',').Append(threaded[i].Item2).Append(']');
						}
						rsb.Append(']');
						rsb.Append(",\"treasures\":[");
						// stop number = position in the stitched chain (-1 for treasures not on it)
						var stopOf = new System.Collections.Generic.Dictionary<int, int>();
						for (int i = 0; i < chain.Count; i++) stopOf[chain[i]] = i;
						for (int i = 0; i < treasures.Count; i++)
						{
							var tr = treasures[i];
							if (i > 0) rsb.Append(',');
							rsb.Append("{\"x\":").Append(tr.x).Append(",\"y\":").Append(tr.y).Append(",\"kind\":\"").Append(tr.kind)
							   .Append("\",\"tier\":\"").Append(tr.tier).Append("\",\"line_x\":").Append(tr.jx).Append(",\"line_y\":").Append(tr.jy)
							   .Append(",\"line_i\":").Append(tr.li)
							   .Append(",\"stop\":").Append(stopOf.TryGetValue(i, out int sn) ? sn : -1)
							   .Append(",\"dig\":").Append(tr.dig).Append(",\"walk\":").Append(tr.walk).Append('}');
						}
						rsb.Append(']');
						// ITINERARY — the visit order, already stitched. The executor walks this and nothing else:
						// go to each stop in turn, collect, continue. No re-deciding what is worth a detour mid-run.
						rsb.Append(",\"itinerary\":[");
						for (int i = 0; i < chain.Count; i++)
						{
							var tr = treasures[chain[i]];
							if (i > 0) rsb.Append(',');
							rsb.Append("{\"x\":").Append(tr.x).Append(",\"y\":").Append(tr.y)
							   .Append(",\"kind\":\"").Append(tr.kind).Append("\",\"line_i\":").Append(tr.li).Append('}');
						}
						rsb.Append("]}");
						body = rsb.ToString();
					}
				}
			}
			else if (path == "/descent_h")
			{
				// POST {"x":..,"y":..} → 上次 /descent_route 那张地狱带场在这一格的 H(=到地狱还要多少)。
				// H 越大离地狱越远,所以"走过头了没"由真实的场判,不由脚本位置判。h:-1 = 不在场里。
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var xM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"x\"\\s*:\\s*(-?\\d+)");
				var yM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"y\"\\s*:\\s*(-?\\d+)");
				if (_descentField == null || !xM.Success || !yM.Success)
				{ body = "{\"error\":\"no_descent_route\"}"; status = 400; }
				else
				{
					int qx = int.Parse(xM.Groups[1].Value), qy = int.Parse(yM.Groups[1].Value);
					body = "{\"h\":" + (_descentField.TryGetValue((qx, qy), out int hv) ? hv : -1) + "}";
				}
			}
			else if (path == "/tile_names")
			{
				// POST {"q":"heart"} → 所有含这个子串的 vanilla TileID 名字,让 agent 先把模糊名字
				// ("生命水晶"→"Heart")解析出来再调 find_tiles,而不是靠猜。省略 q 就列全部。
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var qM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"q\"\\s*:\\s*\"([^\"]*)\"");
				string q = qM.Success ? qM.Groups[1].Value.Trim().ToLowerInvariant() : "";
				var names = new System.Collections.Generic.List<string>();
				for (int t = 0; t < Terraria.ID.TileID.Count; t++)
				{
					if (!Terraria.ID.TileID.Search.TryGetName(t, out string nm) || nm == null) continue;
					if (q.Length == 0 || nm.ToLowerInvariant().Contains(q))
						names.Add(nm);
				}
				var tsb = new StringBuilder("{\"names\":[");
				for (int i = 0; i < names.Count; i++) { if (i > 0) tsb.Append(','); tsb.Append('"').Append(JsonEsc(names[i])).Append('"'); }
				tsb.Append("]}");
				body = tsb.ToString();
			}
			else if (path == "/find_tiles")
			{
				// POST {"name":"Iron","n":5,"max_dist":300} → nearest tiles of that TileID name (exact vanilla name,
				// e.g. Iron/Copper/Gold/Demonite/Containers), expanding-ring scan from the player → sorted by distance.
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var nameM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"name\"\\s*:\\s*\"([A-Za-z0-9_]+)\"");
				var nM2 = System.Text.RegularExpressions.Regex.Match(reqBody, "\"n\"\\s*:\\s*(\\d+)");
				var mdM = System.Text.RegularExpressions.Regex.Match(reqBody, "\"max_dist\"\\s*:\\s*(\\d+)");
				int wantN = nM2.Success ? int.Parse(nM2.Groups[1].Value) : 5;
				int maxD = mdM.Success ? int.Parse(mdM.Groups[1].Value) : 300;
				var pl = Main.LocalPlayer;
				if (!nameM.Success || pl == null)
				{
					body = "{\"error\":\"bad_params\",\"usage\":\"POST /find_tiles {\\\"name\\\":\\\"Iron\\\",\\\"n\\\":5,\\\"max_dist\\\":300}\"}";
					status = 400;
				}
				else if (!Terraria.ID.TileID.Search.TryGetId(nameM.Groups[1].Value, out int wantType))
				{
					body = "{\"error\":\"unknown_tile_name\",\"name\":\"" + JsonEsc(nameM.Groups[1].Value) + "\"}";
					status = 400;
				}
				else
				{
					int pcx = (int)(pl.Center.X / 16f), pcy = (int)(pl.Center.Y / 16f);
					var found = new System.Collections.Generic.List<(int x, int y, string kind)>();
					for (int r = 0; r <= maxD && found.Count < wantN; r++)
						for (int dx = -r; dx <= r && found.Count < wantN; dx++)
						{
							// ring only: interior columns contribute just their top/bottom rows
							int[] dys = System.Math.Abs(dx) == r
								? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Range(-r, 2 * r + 1))
								: (r == 0 ? new[] { 0 } : new[] { -r, r });
							foreach (int dy in dys)
							{
								int x = pcx + dx, y = pcy + dy;
								if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) continue;
								var t = Main.tile[x, y];
								if (!t.HasTile || t.TileType != wantType) continue;
								if (InHive(x, y)) continue;   // 蜂巢里的不捡,进去出不来
								// 箱子是 2x2 一个 TileID,只有左上角那格(frameX%36==0 && frameY%36==0)算锚点。
								// 去重到它,顺便用 frameX 认出是哪种箱子。
								if (wantType == Terraria.ID.TileID.Containers || wantType == Terraria.ID.TileID.Containers2)
								{
									if (t.TileFrameX % 36 != 0 || t.TileFrameY % 36 != 0) continue;
									if (Terraria.Chest.IsLocked(x, y)) continue;
									if (wantType == Terraria.ID.TileID.Containers && t.TileFrameX / 36 == 16) continue;
									found.Add((x, y, ChestKindName(wantType, t.TileFrameX / 36)));
								}
								else found.Add((x, y, null));
								if (found.Count >= wantN) break;
							}
						}
					var fsb = new StringBuilder("{\"tiles\":[");
					for (int i = 0; i < found.Count; i++)
					{
						if (i > 0) fsb.Append(',');
						fsb.Append($"{{\"x\":{found[i].x},\"y\":{found[i].y},\"dist\":{System.Math.Abs(found[i].x - pcx) + System.Math.Abs(found[i].y - pcy)}");
						if (found[i].kind != null) fsb.Append($",\"kind\":\"{JsonEsc(found[i].kind)}\"");
						fsb.Append("}");
					}
					fsb.Append("]}");
					body = fsb.ToString();
				}
			}
			else if (path == "/probe_cell")
			{
				// 单格空间查询:有没有背景墙、能不能放方块/平台、这格通不通。
				// 回答"有背景墙吗/这儿能放平台吗",省得对结构两眼一抹黑。
				string rb = ReadBody(ctx).Replace(" ", "");
				var xm = System.Text.RegularExpressions.Regex.Match(rb, "\"x\"\\s*:\\s*(-?\\d+)");
				var ym = System.Text.RegularExpressions.Regex.Match(rb, "\"y\"\\s*:\\s*(-?\\d+)");
				if (!xm.Success || !ym.Success) { body = "{\"error\":\"bad_params\"}"; status = 400; }
				else
				{
					int x = int.Parse(xm.Groups[1].Value), y = int.Parse(ym.Groups[1].Value);
					body = ProbeCellJson(x, y);
				}
			}
			// ── PREDICATES ── 纯查询无副作用。以前这些答案要么写死在脚本里,要么让 LLM 猜。见 Predicates.cs。
			// /nav_h — 一片矩形里规划器自己的 H 和 can_stand。场外的格子返回 null,而"哪些格在场外"本身就是答案。
			else if (path == "/nav_h")
			{
				string rb = ReadBody(ctx).Replace(" ", "");
				int G(string k, int dflt)
				{
					var m = System.Text.RegularExpressions.Regex.Match(rb, "\"" + k + "\"\\s*:\\s*(-?\\d+)");
					return m.Success ? int.Parse(m.Groups[1].Value) : dflt;
				}
				int x0 = G("x0", 0), x1 = G("x1", x0), y0 = G("y0", 0), y1 = G("y1", y0);
				if (x1 < x0 || y1 < y0 || (x1 - x0) > 200 || (y1 - y0) > 200)
				{ body = "{\"error\":\"bad_range\"}"; status = 400; }
				else
				{
					var sb = new System.Text.StringBuilder();
					MazeWand.TryPeek(x0, y0, out _, out var goal, out int cells);
					sb.Append("{\"goal\":[").Append(goal.gx).Append(',').Append(goal.gy).Append(']')
					  .Append(",\"field_cells\":").Append(cells).Append(",\"cells\":[");
					bool f1 = true;
					for (int y = y0; y <= y1; y++)
						for (int x = x0; x <= x1; x++)
						{
							bool has = MazeWand.TryPeek(x, y, out int h, out _, out _);
							if (!f1) sb.Append(',');
							f1 = false;
							sb.Append("{\"x\":").Append(x).Append(",\"y\":").Append(y)
							  .Append(",\"h\":").Append(has ? h.ToString() : "null")
							  .Append(",\"stand\":").Append(Predicates.CanStand(x, y) ? "true" : "false").Append('}');
						}
					sb.Append("]}");
					body = sb.ToString();
				}
			}
			else if (path == "/body_cols")
			{
				// 玩家此刻压住哪几列 —— 20px 必跨 2 列,亚像素位置决定是 2 列还是 3 列。
				// 每处自己 floor 一遍是坐标歧义的源头(往自己脚底下搭桥就是这么来的),统一从这里读。
				var pl = Main.LocalPlayer;
				if (pl == null || !pl.active) { body = "{\"error\":\"no_player\"}"; status = 400; }
				else
				{
					var (bl, br) = Predicates.BodyCols(pl);
					int feetRow = (int)((pl.position.Y + pl.height) / 16f);
					var sb = new System.Text.StringBuilder();
					sb.Append("{\"left\":").Append(bl).Append(",\"right\":").Append(br)
					  .Append(",\"span\":").Append(br - bl + 1)
					  .Append(",\"feet_row\":").Append(feetRow)
					  .Append(",\"center_col\":").Append(Predicates.PillarCol(pl))
					  .Append(",\"px\":").Append(pl.position.X.ToString("0.##"))
					  .Append(",\"cols\":[");
					for (int c = bl; c <= br; c++)
					{
						if (c > bl) sb.Append(',');
						sb.Append("{\"x\":").Append(c)
						  .Append(",\"support\":").Append(Predicates.IsGround(c, feetRow) ? "true" : "false")
						  .Append('}');
					}
					sb.Append("]}");
					body = sb.ToString();
				}
			}
			else if (path == "/can_stand")
			{
				// Every geometric predicate for one cell at once: can_stand plus the measurements behind it, so a
				// caller learns WHY it can't stand there (no ground / no headroom / lava) instead of a bare false.
				string rb = ReadBody(ctx).Replace(" ", "");
				var xm = System.Text.RegularExpressions.Regex.Match(rb, "\"x\"\\s*:\\s*(-?\\d+)");
				var ym = System.Text.RegularExpressions.Regex.Match(rb, "\"y\"\\s*:\\s*(-?\\d+)");
				var wm = System.Text.RegularExpressions.Regex.Match(rb, "\"width_cap\"\\s*:\\s*(\\d+)");
				var hm = System.Text.RegularExpressions.Regex.Match(rb, "\"head_cap\"\\s*:\\s*(\\d+)");
				if (!xm.Success || !ym.Success) { body = "{\"error\":\"bad_params\"}"; status = 400; }
				else
					body = Predicates.CellJson(int.Parse(xm.Groups[1].Value), int.Parse(ym.Groups[1].Value),
						wm.Success ? int.Parse(wm.Groups[1].Value) : 32,
						hm.Success ? int.Parse(hm.Groups[1].Value) : 16);
			}
			else if (path == "/scan_flat")
			{
				// Find a build site: nearest spot with `w` standable columns, `h` rows of headroom, no hazard within
				// `hazard_r`. THE replacement for a hardcoded build coordinate — same question, terrain-dependent answer.
				string rb = ReadBody(ctx).Replace(" ", "");
				int Get(string k, int dflt)
				{
					var m = System.Text.RegularExpressions.Regex.Match(rb, "\"" + k + "\"\\s*:\\s*(-?\\d+)");
					return m.Success ? int.Parse(m.Groups[1].Value) : dflt;
				}
				var p = Main.LocalPlayer;
				int fx = Get("from_x", p != null ? ActExecutor.OriginCx(p) : 0);
				int fy = Get("from_y", p != null ? ActExecutor.OriginCy(p) : 0);
				int w = Get("w", 14), h = Get("h", 12), hr = Get("hazard_r", 0), range = Get("range", 200);
				bool found = Predicates.ScanFlat(fx, fy, w, h, hr, range, out int hx, out int hy, out int scanned);
				var sb = new System.Text.StringBuilder();
				sb.Append("{\"found\":").Append(found ? "true" : "false");
				sb.Append(",\"from\":[").Append(fx).Append(',').Append(fy).Append(']');
				sb.Append(",\"want\":{\"w\":").Append(w).Append(",\"h\":").Append(h).Append(",\"hazard_r\":").Append(hr).Append('}');
				if (found) sb.Append(",\"at\":[").Append(hx).Append(',').Append(hy).Append("],\"span\":[")
					.Append(hx).Append(',').Append(hx + w - 1).Append(']');
				sb.Append(",\"scanned\":").Append(scanned).Append('}');
				body = sb.ToString();
			}
			else if (path == "/path_cost")
			{
				// 从玩家现在的位置走到 (x,y) 要挖几格、走几格 —— 判断"值不值得绕过去"要看真实路径。
				// find_tiles 的 max_dist 是直线距离,直线 25 格的东西可能隔着山要绕几百格。
				string rb = ReadBody(ctx).Replace(" ", "");
				var xm = System.Text.RegularExpressions.Regex.Match(rb, "\"x\"\\s*:\\s*(-?\\d+)");
				var ym = System.Text.RegularExpressions.Regex.Match(rb, "\"y\"\\s*:\\s*(-?\\d+)");
				var pc = Main.LocalPlayer;
				if (!xm.Success || !ym.Success || pc == null)
				{ body = "{\"ok\":false,\"reason\":\"bad_request\"}"; status = 400; }
				else
				{
					int tx = int.Parse(xm.Groups[1].Value), ty = int.Parse(ym.Groups[1].Value);
					int scx = ActExecutor.OriginCx(pc), scy = ActExecutor.OriginCy(pc);
					// 箱子/矿是【实心格】,人永远站不进去。以它本身为种子建场,种子那格不可通行,
					// 场铺不出来,玩家格永远没值 → 每个箱子都报 unreachable。要的是"够得着",不是"站进去"。
					var seeds = new System.Collections.Generic.List<(int x, int y)>();
					for (int dx = -1; dx <= 1; dx++)
						for (int dy = -1; dy <= 1; dy++)
						{
							if (dx == 0 && dy == 0) continue;
							int ax = tx + dx, ay = ty + dy;
							if (!Predicates.IsSolid(ax, ay)) seeds.Add((ax, ay));
						}
					if (seeds.Count == 0) seeds.Add((tx, ty));
					int pad = 120;
					var fld = MazeWand.BuildFieldMulti(seeds,
						System.Math.Min(tx, scx) - pad, System.Math.Max(tx, scx) + pad,
						System.Math.Min(ty, scy) - pad, System.Math.Max(ty, scy) + pad);
					if (!fld.ContainsKey((scx, scy)))
						body = "{\"ok\":false,\"reason\":\"unreachable\"}";
					else
					{
						int dig = 0, walk = 0;
						var cur = (x: scx, y: scy);
						var seen = new System.Collections.Generic.HashSet<(int, int)> { cur };
						for (int step = 0; step < 4000; step++)
						{
							if (seeds.Contains((cur.x, cur.y))) break;
							if (!fld.TryGetValue((cur.x, cur.y), out int hc) || hc == 0) break;
							int bn = hc; var best = cur;
							foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
							{
								var n = (x: cur.x + dx, y: cur.y + dy);
								if (fld.TryGetValue((n.x, n.y), out int dn) && dn < bn) { bn = dn; best = n; }
							}
							if (best.x == cur.x && best.y == cur.y) break;
							if (!seen.Add((best.x, best.y))) break;
							if (MazeWand.StepCostPublic(cur.x, cur.y, best.x, best.y) >= 80) dig++; else walk++;
							cur = best;
						}
						body = "{\"ok\":true,\"dig\":" + dig + ",\"walk\":" + walk + "}";
					}
				}
			}
			else if (path == "/shop_list")
			{
				// 商店开着时:背包里每一格值多少钱(【卖价】=原价/5,不是 item.value),
				// 外加身上有多少钱。卖东西凑钱要先看这个
				var sp = Main.LocalPlayer;
				var sb3 = new System.Text.StringBuilder();
				sb3.Append("{\"open\":").Append(sp != null && sp.talkNPC >= 0 ? "true" : "false");
				sb3.Append(",\"money\":").Append(sp != null ? Shop.Money(sp) : 0);
				sb3.Append(",\"money_text\":\"").Append(JsonEsc(sp != null ? Shop.Coins(Shop.Money(sp)) : "")).Append("\"");
				sb3.Append(",\"items\":[");
				bool first3 = true;
				if (sp != null)
					for (int i = 0; i < sp.inventory.Length; i++)
					{
						var it = sp.inventory[i];
						if (it == null || it.IsAir) continue;
						long unit = Shop.SellUnit(sp, it);
						if (unit <= 0) continue;              // 卖不掉的不列,省得上层再筛一遍
						if (!first3) sb3.Append(',');
						first3 = false;
						sb3.Append("{\"slot\":").Append(i)
						   .Append(",\"name\":\"").Append(JsonEsc(it.Name)).Append("\"")
						   .Append(",\"type\":").Append(it.type)
						   .Append(",\"stack\":").Append(it.stack)
						   .Append(",\"unit\":").Append(unit)
						   .Append(",\"total\":").Append(unit * it.stack)
						   .Append(",\"text\":\"").Append(JsonEsc(Shop.Coins(unit * it.stack))).Append("\"}");
					}
				sb3.Append("]}");
				body = sb3.ToString();
			}
			else if (path == "/shop_sell")
			{
				// {"slot":N} 卖掉那一格。整格一起卖 —— 原版 SellItem 就是这个粒度,
				// 而且它塞不下钱时会把整个背包回滚,不会东西没了钱也没拿到
				string rss = ReadBody(ctx).Replace(" ", "");
				var mss = System.Text.RegularExpressions.Regex.Match(rss, "\"slot\"\\s*:\\s*(\\d+)");
				var sp2 = Main.LocalPlayer;
				if (!mss.Success) body = "{\"ok\":false,\"reason\":\"need_slot\"}";
				else if (sp2 == null) body = "{\"ok\":false,\"reason\":\"no_player\"}";
				else
				{
					long before = Shop.Money(sp2);
					bool oks = Shop.Sell(sp2, int.Parse(mss.Groups[1].Value), out string whys);
					long after = Shop.Money(sp2);
					body = oks
						? "{\"ok\":true,\"gained\":" + (after - before) + ",\"money\":" + after
							+ ",\"money_text\":\"" + JsonEsc(Shop.Coins(after)) + "\"}"
						: "{\"ok\":false,\"reason\":\"" + JsonEsc(whys) + "\"}";
				}
			}
			else if (path == "/hell_run")
			{
				// 地狱那一整套:算线 → 选址 → 去桥起点 → 盖房 → 铺桥 → 肉山准备 → 开打。
				// 全部编排在 mod 里,python 只管触发和轮询 —— 和 /build_house 一个路子。
				// 真正的活在主线程做(要读地形算线),这里只入队。
				//
				// POST {"teleport":true} → 先把人放到地狱再开跑。【只测地狱这一段】时用:
				// 不用每次都从地表跑一遍丛林+下降。传哪由 mod 算(TeleportToHell),
				// python 照旧只是一次触发
				string hrb = ReadBody(ctx).Replace(" ", "");
				if (hrb.Contains("\"teleport\":true")) _hellTpQueue.Enqueue(true);
				HellRunStart = "";
				_hellRunQueue.Enqueue(true);
				body = "{\"accepted\":true,\"note\":\"poll /hell_run_status\"}";
			}
			else if (path == "/teleport")
			{
				// POST {"x":列,"y":行} → 把人放到那一格上站着。
				// 只为【跳过前面的环节单独测后面】用:比如只测地狱那一段,不想每次都从地表跑一遍
				string tb = ReadBody(ctx).Replace(" ", "");
				var txm = System.Text.RegularExpressions.Regex.Match(tb, "\"x\"\\s*:\\s*(-?\\d+)");
				var tym = System.Text.RegularExpressions.Regex.Match(tb, "\"y\"\\s*:\\s*(-?\\d+)");
				if (!txm.Success || !tym.Success)
					body = "{\"ok\":false,\"reason\":\"need x and y\"}";
				else
				{
					int tx = int.Parse(txm.Groups[1].Value), ty = int.Parse(tym.Groups[1].Value);
					if (tx < 1 || ty < 1 || tx >= Main.maxTilesX - 1 || ty >= Main.maxTilesY - 1)
						body = "{\"ok\":false,\"reason\":\"out of world\"}";
					else { _tpQueue.Enqueue((tx, ty)); body = "{\"ok\":true,\"x\":" + tx + ",\"y\":" + ty + "}"; }
				}
			}
			else if (path == "/hell_run_status")
			{
				string ph = StateSnapshotPlayer.HellRunPhase();
				body = "{\"phase\":\"" + JsonEsc(ph) + "\""
					 + ",\"running\":" + (ph != "idle" ? "true" : "false")
					 + ",\"start_error\":\"" + JsonEsc(HellRunStart) + "\""
					 + ",\"wof_outcome\":\"" + JsonEsc(WofPrep.Outcome) + "\""
					 + ",\"wof_reason\":\"" + JsonEsc(WofPrep.Reason) + "\"}";
			}
			else if (path == "/hell_run_stop")
			{
				StateSnapshotPlayer.StopHellRun();
				body = "{\"ok\":true}";
			}
			else if (path == "/build_house")
			{
				// 盖房子的唯一入口。rooms=4 是地表那座 21 宽的,rooms=1 是肉山桥起点那间。
				// 编排整个在 mod 里(HouseBuilder),python 只负责选址和触发。
				string rbh = ReadBody(ctx).Replace(" ", "");
				int GetH(string k, int dflt)
				{
					var m = System.Text.RegularExpressions.Regex.Match(rbh, "\"" + k + "\"\\s*:\\s*(-?\\d+)");
					return m.Success ? int.Parse(m.Groups[1].Value) : dflt;
				}
				var php = Main.LocalPlayer;
				int rooms = GetH("rooms", 4);
				int hdir = GetH("dir", 1);
				int hax = GetH("x", php != null ? ActExecutor.OriginCx(php) : 0);
				int hay = GetH("y", php != null ? ActExecutor.OriginCy(php) + 1 : 0);
				bool okh = HouseBuilder.Start(rooms, hdir, hax, hay, out string whyh);
				body = okh ? "{\"accepted\":true,\"rooms\":" + rooms + ",\"corner\":[" + hax + "," + hay + "],\"note\":\"poll /build_house_status\"}"
						   : "{\"accepted\":false,\"reason\":\"" + JsonEsc(whyh) + "\"}";
			}
			else if (path == "/build_house_status")
			{
				body = HouseBuilder.StatusJson();
			}
			else if (path == "/build_house_stop")
			{
				HouseBuilder.Stop();
				body = "{\"ok\":true}";
			}
			else if (path == "/scan_house")
			{
				// 房子就是一个 w×h 的矩形,(x,y) 是左下角。除了"里面全空"没有别的条件:
				// 脚下是不是实地不管,悬空也行 —— 施工时垫平台上去,垫多高跟选址无关。
				string rb = ReadBody(ctx).Replace(" ", "");
				int Get(string k, int dflt)
				{
					var m = System.Text.RegularExpressions.Regex.Match(rb, "\"" + k + "\"\\s*:\\s*(-?\\d+)");
					return m.Success ? int.Parse(m.Groups[1].Value) : dflt;
				}
				var ph = Main.LocalPlayer;
				int fx = Get("from_x", ph != null ? ActExecutor.OriginCx(ph) : 0);
				int fy = Get("from_y", ph != null ? ActExecutor.OriginCy(ph) : 0);
				int w = Get("w", 21), h = Get("h", 10), range = Get("range", 200);
				bool fnd = Predicates.ScanHouse(fx, fy, w, h, range, out int hx2, out int hy2, out int sc2);
				var sbh = new System.Text.StringBuilder();
				sbh.Append("{\"found\":").Append(fnd ? "true" : "false");
				sbh.Append(",\"from\":[").Append(fx).Append(',').Append(fy).Append(']');
				sbh.Append(",\"want\":{\"w\":").Append(w).Append(",\"h\":").Append(h).Append('}');
				// at = 矩形左下角。top/right 是另外两条边,省得调用方自己算错(都含自己那格)。
				if (fnd) sbh.Append(",\"at\":[").Append(hx2).Append(',').Append(hy2).Append(']')
					.Append(",\"top\":").Append(hy2 - h + 1)
					.Append(",\"right\":").Append(hx2 + w - 1);
				sbh.Append(",\"scanned\":").Append(sc2).Append('}');
				// 把结论画出来。找到了画选中的框;没找到画出发点那个框,一眼看出被什么挡了。
				{
					int bx = fnd ? hx2 : fx, by = fnd ? hy2 : fy;
					Predicates.VisualizeBox(bx, by, w, h, fnd ? $"house {w}x{h}" : "NO SITE (from here)");
				}
				body = sbh.ToString();
			}
			else if (path == "/room_check")
			{
				// Vanilla's own housing test, flood-filled from a point INSIDE the room (not a rectangle). Reports
				// which of door/table/chair/torch is missing — "no door" is a diagnosis, "the NPC didn't move in" isn't.
				string rb = ReadBody(ctx).Replace(" ", "");
				var xm = System.Text.RegularExpressions.Regex.Match(rb, "\"x\"\\s*:\\s*(-?\\d+)");
				var ym = System.Text.RegularExpressions.Regex.Match(rb, "\"y\"\\s*:\\s*(-?\\d+)");
				if (!xm.Success || !ym.Success) { body = "{\"error\":\"bad_params\"}"; status = 400; }
				else body = Predicates.RoomJson(int.Parse(xm.Groups[1].Value), int.Parse(ym.Groups[1].Value));
			}
			else if (path == "/have")
			{
				// How many of an item id are held. The material precondition, asked directly.
				string rb = ReadBody(ctx).Replace(" ", "");
				var im = System.Text.RegularExpressions.Regex.Match(rb, "\"id\"\\s*:\\s*(\\d+)");
				if (!im.Success) { body = "{\"error\":\"bad_params\"}"; status = 400; }
				else
				{
					int id = int.Parse(im.Groups[1].Value);
					var nm = System.Text.RegularExpressions.Regex.Match(rb, "\"n\"\\s*:\\s*(\\d+)");
					int want = nm.Success ? int.Parse(nm.Groups[1].Value) : 1;
					int has = Predicates.Have(id);
					body = "{\"id\":" + id + ",\"have\":" + has + ",\"want\":" + want +
						   ",\"enough\":" + (has >= want ? "true" : "false") + "}";
				}
			}
			else if (path == "/npc_find")
			{
				// Where an NPC is, by type id (omit type for all actives). Needed to wait for a merchant to arrive and
				// to dig out the cell under the Guide.
				string rb = ReadBody(ctx).Replace(" ", "");
				var tm = System.Text.RegularExpressions.Regex.Match(rb, "\"type\"\\s*:\\s*(-?\\d+)");
				body = Predicates.NpcJson(tm.Success ? int.Parse(tm.Groups[1].Value) : -1);
			}
			else if (path == "/measure")
			{
				// How big is the connected blob of the same tile at (x,y): tree height, ore-vein size, cavity — the
				// "这棵树多高 / 这堆矿多大" question. For an empty cell it measures the open cavity instead.
				string rb = ReadBody(ctx).Replace(" ", "");
				var xm = System.Text.RegularExpressions.Regex.Match(rb, "\"x\"\\s*:\\s*(-?\\d+)");
				var ym = System.Text.RegularExpressions.Regex.Match(rb, "\"y\"\\s*:\\s*(-?\\d+)");
				if (!xm.Success || !ym.Success) { body = "{\"error\":\"bad_params\"}"; status = 400; }
				else
				{
					int x = int.Parse(xm.Groups[1].Value), y = int.Parse(ym.Groups[1].Value);
					body = MeasureBlobJson(x, y);
				}
			}
			else
			{
				body = "{\"error\":\"not_found\"}";
				status = 404;
			}

			byte[] bytes = Encoding.UTF8.GetBytes(body);
			ctx.Response.StatusCode = status;
			ctx.Response.ContentType = "application/json";
			ctx.Response.ContentLength64 = bytes.Length;
			ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
			ctx.Response.OutputStream.Close();
		}

		static string ReadBody(HttpListenerContext ctx)
		{
			using var sr = new System.IO.StreamReader(ctx.Request.InputStream);
			return sr.ReadToEnd();
		}

		static bool CellInBounds(int x, int y) =>
			x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;

		// 单格的结构事实:背景墙、通不通、能不能放方块/平台。放置判据取实用那版,不是完整的 worldgen 检查:
		// 平台在任何空格都能放,实心方块要空格且至少一个邻居是实心/平台可以贴。
		static string ProbeCellJson(int x, int y)
		{
			if (!CellInBounds(x, y)) return "{\"error\":\"out_of_bounds\"}";
			var t = Main.tile[x, y];
			bool hasTile = t.HasTile;
			int wall = t.WallType;
			bool open = !hasTile || !Main.tileSolid[t.TileType];

			bool NeighbourSupport(int nx, int ny)
			{
				if (!CellInBounds(nx, ny)) return false;
				var n = Main.tile[nx, ny];
				return n.HasTile && (Main.tileSolid[n.TileType] || Terraria.ID.TileID.Sets.Platforms[n.TileType]);
			}
			bool canPlatform = open;
			bool canBlock = open && (NeighbourSupport(x - 1, y) || NeighbourSupport(x + 1, y)
				|| NeighbourSupport(x, y - 1) || NeighbourSupport(x, y + 1));

			var sb = new StringBuilder("{");
			sb.Append("\"has_tile\":").Append(hasTile ? "true" : "false");
			if (hasTile) sb.Append(",\"tile_type\":").Append(t.TileType);
			sb.Append(",\"open\":").Append(open ? "true" : "false");
			sb.Append(",\"wall\":").Append(wall);
			sb.Append(",\"has_wall\":").Append(wall > 0 ? "true" : "false");
			sb.Append(",\"can_place_platform\":").Append(canPlatform ? "true" : "false");
			sb.Append(",\"can_place_block\":").Append(canBlock ? "true" : "false");
			sb.Append('}');
			return sb.ToString();
		}

		// Size of the connected same-type blob at (x,y): tree height / ore-vein size, or — for an open cell — the
		// open cavity. BFS with a cap so a huge open area can't run away. Returns bounding box + cell count.
		static string MeasureBlobJson(int x, int y)
		{
			if (!CellInBounds(x, y)) return "{\"error\":\"out_of_bounds\"}";
			var start = Main.tile[x, y];
			bool measuringTile = start.HasTile;
			int wantType = measuringTile ? start.TileType : -1;
			const int Cap = 400;

			var seen = new System.Collections.Generic.HashSet<(int, int)>();
			var q = new System.Collections.Generic.Queue<(int, int)>();
			q.Enqueue((x, y)); seen.Add((x, y));
			int minX = x, maxX = x, minY = y, maxY = y, count = 0;
			bool Match(int cx, int cy)
			{
				var c = Main.tile[cx, cy];
				return measuringTile ? (c.HasTile && c.TileType == wantType)
									  : (!c.HasTile || !Main.tileSolid[c.TileType]);
			}
			while (q.Count > 0 && count < Cap)
			{
				var (cx, cy) = q.Dequeue();
				if (!CellInBounds(cx, cy) || !Match(cx, cy)) continue;
				count++;
				if (cx < minX) minX = cx; if (cx > maxX) maxX = cx;
				if (cy < minY) minY = cy; if (cy > maxY) maxY = cy;
				foreach (var (nx, ny) in new[] { (cx - 1, cy), (cx + 1, cy), (cx, cy - 1), (cx, cy + 1) })
					if (!seen.Contains((nx, ny)) && CellInBounds(nx, ny)) { seen.Add((nx, ny)); q.Enqueue((nx, ny)); }
			}
			var sb = new StringBuilder("{");
			sb.Append("\"kind\":\"").Append(measuringTile ? "solid" : "cavity").Append("\"");
			sb.Append(",\"cells\":").Append(count);
			sb.Append(",\"width\":").Append(maxX - minX + 1);
			sb.Append(",\"height\":").Append(maxY - minY + 1);
			sb.Append(",\"capped\":").Append(count >= Cap ? "true" : "false");
			sb.Append('}');
			return sb.ToString();
		}

		// signature tiles that identify a biome — shared by /find_biome and /find_descent
		static ushort[] BiomeSig(string biome) => biome switch
		{
			"jungle" => new[] { Terraria.ID.TileID.JungleGrass },
			"snow" or "ice" or "tundra" => new[] { Terraria.ID.TileID.SnowBlock, Terraria.ID.TileID.IceBlock },
			"desert" => new[] { Terraria.ID.TileID.Sandstone, Terraria.ID.TileID.HardenedSand },
			"dungeon" => new[] { Terraria.ID.TileID.BlueDungeonBrick, Terraria.ID.TileID.GreenDungeonBrick, Terraria.ID.TileID.PinkDungeonBrick },
			"corruption" => new[] { Terraria.ID.TileID.CorruptGrass, Terraria.ID.TileID.Ebonstone },
			"crimson" => new[] { Terraria.ID.TileID.CrimsonGrass, Terraria.ID.TileID.Crimstone },
			"hallow" => new[] { Terraria.ID.TileID.HallowedGrass, Terraria.ID.TileID.Pearlstone },
			_ => null,
		};

		class DescentData
		{
			public int EntX, EntY, Cost, Cands;
			public System.Collections.Generic.Dictionary<(int, int), int> Field;
		}

		// I 键预览:主道 → 地狱的线 + 桥 + 房子,只画不走。ComputeDescent 和描线逻辑都在这个类里,
		// 所以调用方在外面重写一遍毫无意义 —— 那样两份代码必然漂移。
		public static bool PreviewDescentAndBridge(string biome, out string msg)
		{
			msg = "";
			var sig = BiomeSig(biome);
			if (sig == null) { msg = $"不认识的群系 {biome}"; return false; }
			var dd = ComputeDescent(sig, out string why);
			if (dd == null) { msg = $"找不到主道:{why}"; return false; }

			var line = new System.Collections.Generic.List<(int x, int y)>();
			var pl = Main.LocalPlayer;
			if (pl != null)
			{
				int pcx = ActExecutor.OriginCx(pl), pcy = ActExecutor.OriginCy(pl);
				if (System.Math.Abs(pcx - dd.EntX) + System.Math.Abs(pcy - dd.EntY) > 4)
				{
					var af = MazeWand.BuildField(dd.EntX, dd.EntY, pcx, pcy);
					var ac = (x: pcx, y: pcy);
					var aseen = new System.Collections.Generic.HashSet<(int, int)>();
					for (int step = 0; step < 20000; step++)
					{
						line.Add(ac);
						if (!aseen.Add(ac)) break;
						if (ac.x == dd.EntX && ac.y == dd.EntY) break;
						if (!af.TryGetValue((ac.x, ac.y), out int ah)) break;
						int bn = ah; var bc = ac; bool moved = false;
						foreach (var (dx2, dy2) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
						{
							var n = (x: ac.x + dx2, y: ac.y + dy2);
							if (af.TryGetValue((n.x, n.y), out int nh) && nh < bn) { bn = nh; bc = n; moved = true; }
						}
						if (!moved) break;
						ac = bc;
					}
				}
			}
			var cur = (dd.EntX, dd.EntY);
			var seen = new System.Collections.Generic.HashSet<(int, int)>();
			for (int step = 0; step < 20000; step++)
			{
				line.Add(cur);
				if (!seen.Add(cur)) break;
				if (dd.Field.TryGetValue(cur, out int hc) && hc == 0) break;
				int bestN = int.MaxValue; var best = cur;
				foreach (var (dx2, dy2) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
				{
					var n = (cur.Item1 + dx2, cur.Item2 + dy2);
					if (!dd.Field.TryGetValue(n, out int dn)) continue;
					if (dn < bestN) { bestN = dn; best = n; }
				}
				if (best == cur) break;
				cur = best;
			}

			var tail = line[line.Count - 1];
			int hdir = tail.Item1 < Main.maxTilesX / 2 ? 1 : -1;
			var hl = HellLine.Compute(tail.Item1, hdir);

			var vis = new System.Collections.Generic.List<(int, int, Microsoft.Xna.Framework.Color)>();
			var routeC = new Microsoft.Xna.Framework.Color(0, 200, 255, 120);
			foreach (var (lx, ly) in line) vis.Add((lx, ly, routeC));
			if (hl.Found)
			{
				var bridgeC = new Microsoft.Xna.Framework.Color(120, 255, 120, 200);
				var houseC = new Microsoft.Xna.Framework.Color(255, 180, 0, 240);
				foreach (var (lx, ly) in hl.Line) vis.Add((lx, ly, bridgeC));
				for (int k = 0; k < HouseBuilder.RoomWidth + 1; k++)
					vis.Add((hl.HouseX + hdir * k, hl.HouseY, houseC));
			}
			PathVisSystem.SetTiles(vis, 7200);

			msg = hl.Found
				? $"主道{line.Count}格→落点({tail.Item1},{tail.Item2}) | 房子({hl.HouseX},{hl.HouseY}) 岩浆{hl.HouseLavaCols}/6 挖{hl.DigCells}"
				: $"主道{line.Count}格→落点({tail.Item1},{tail.Item2}) | 桥算不出来:{hl.Why}";
			DiagLog.Write($"[preview] {msg}");
			return true;
		}

		// 宝藏值多少 = 它能撑起的【绕道格数】上限。水晶=金箱>>木箱。
		// 木箱 90:不值得为它专门绕路,但顺路必开 —— 40 的时候人贴着走过去都不开,过分了。
		const int ValueHeart = 220, ValueChest = 220, ValueWoodChest = 90;
		static int TreasureValue(string kind) => kind == "heart" ? ValueHeart
			: kind == "wood_chest" ? ValueWoodChest : ValueChest;
		// 挖一格约等于走这么多格(场里 DigSide 26 : MoveSide 3)。绕道折算成"走了多远"用。
		const int DigWalkRatio = 9;
		// 全程绕路预算 = 主线长度的这个比例。用完只走主线 —— 这是"不能光顾着收集"的闸。
		const float DetourBudgetFrac = 3.0f;
		// DP 把预算切成几档。80 档下每档约几格,够分出宝藏之间的差别,n²B 也就几十万次。
		const int BudgetSteps = 80;
		static System.Collections.Generic.Dictionary<(int, int), int> _descentField;

		// 地表线 S(x):从天上往下第一块【底下 20 格里有 ≥15 格实心】的砖(树冠屋顶那种薄壳跳过),
		// 再做宽 64 的闭运算,免得坑底竖井内壁冒充地表。然后从地狱带往上 flood,H 最小的地表格就是最便宜的入口。
		static DescentData ComputeDescent(ushort[] sigTypes, out string failReason)
		{
			failReason = "";
			var want = new System.Collections.Generic.HashSet<ushort>(sigTypes);
			int surfaceCap = (int)Main.worldSurface;
			int sMinX = int.MaxValue, sMaxX = int.MinValue;
			for (int x = 0; x < Main.maxTilesX; x += 2)
				for (int y = 0; y < surfaceCap; y += 2)
				{
					var t = Main.tile[x, y];
					if (!t.HasTile || !want.Contains(t.TileType)) continue;
					if (x < sMinX) sMinX = x;
					if (x > sMaxX) sMaxX = x;
				}
			if (sMinX > sMaxX) { failReason = "no_surface_biome"; return null; }

			int w = sMaxX - sMinX + 1;
			int hellCap = Main.maxTilesY - 200;
			var raw = new int[w];
			for (int i = 0; i < w; i++)
			{
				int x = sMinX + i, y = 0;
				raw[i] = surfaceCap;
				while (y < hellCap)
				{
					while (y < hellCap && !(Main.tile[x, y].HasTile && Main.tileSolid[Main.tile[x, y].TileType])) y++;
					if (y >= hellCap) break;
					int solidBelow = 0;
					for (int k = 1; k <= 20; k++)
						if (Main.tile[x, y + k].HasTile && Main.tileSolid[Main.tile[x, y + k].TileType]) solidBelow++;
					if (solidBelow >= 15) { raw[i] = y; break; }
					y++;
				}
			}
			const int r = 32;
			var ero = new int[w];
			var surf = new int[w];
			for (int i = 0; i < w; i++)
			{
				int m = int.MaxValue;
				for (int k = System.Math.Max(0, i - r); k <= System.Math.Min(w - 1, i + r); k++) if (raw[k] < m) m = raw[k];
				ero[i] = m;
			}
			for (int i = 0; i < w; i++)
			{
				int m = int.MinValue;
				for (int k = System.Math.Max(0, i - r); k <= System.Math.Min(w - 1, i + r); k++) if (ero[k] > m) m = ero[k];
				surf[i] = m;
			}
			// 终点必须【脚下有方块】:原来只要是空格就算到,而这一带正是天花板层,
			// 于是线停在半空,人导航完悬着。人占 3 格高,所以上面两格也得是空的。
			var sources = new System.Collections.Generic.List<(int x, int y)>();
			int hellTop = Main.maxTilesY - 190, hellBot = Main.maxTilesY - 150;
			for (int x = sMinX; x <= sMaxX; x += 2)
				for (int y = hellTop; y <= hellBot; y++)
				{
					if (!Predicates.IsGround(x, y + 1)) continue;
					bool room = true;
					for (int br = 0; br < 3; br++)
						if (Predicates.IsSolid(x, y - br) || Predicates.IsAnyLiquid(x, y - br)) { room = false; break; }
					if (!room) continue;
					sources.Add((x, y));
				}
			if (sources.Count == 0) { failReason = "no_hell_sources"; return null; }

			int minY = surf[0];
			for (int i = 1; i < w; i++) if (surf[i] < minY) minY = surf[i];
			minY = System.Math.Max(0, minY - 10);
			var field = MazeWand.BuildFieldMulti(sources,
				System.Math.Max(0, sMinX - 60), System.Math.Min(Main.maxTilesX - 1, sMaxX + 60),
				minY, Main.maxTilesY - 1);
			int bestX = -1, bestY = -1, bestH = int.MaxValue, cands = 0;
			for (int i = 0; i < w; i += 2)
			{
				int ex = sMinX + i, ey = surf[i] - 1;
				cands++;
				// the stand cell over a pit mouth is air — the field prices it; try one higher as fallback
				if (!field.TryGetValue((ex, ey), out int h) && !field.TryGetValue((ex, ey - 1), out h)) continue;
				if (h < bestH) { bestH = h; bestX = ex; bestY = ey; }
			}
			if (bestX < 0) { failReason = "no_route"; return null; }
			return new DescentData { EntX = bestX, EntY = bestY, Cost = bestH, Cands = cands, Field = field };
		}

		// one step of /act. Keys are flags; cursor is rel (follows the player each frame) or at (origin frozen at step
		// start); until is mandatory and names the progress quantity the executor watches for stalling.
		static ActStep ParseActStep(string o)
		{
			var s = new ActStep();
			bool Key(string k) => o.Contains("\"" + k + "\"");
			if (Key("left")) s.Left = true;
			if (Key("right")) s.Right = true;
			if (Key("up")) s.Up = true;
			if (Key("down")) s.Down = true;
			if (Key("jump")) s.Jump = true;
			if (Key("use_tile")) s.UseTile = true;
			if (Key("throw")) s.Throw = true;
			if (Key("hook")) s.Hook = true;
			if (Key("mount")) s.Mount = true;
			s.UseItem = System.Text.RegularExpressions.Regex.IsMatch(o, "\"use\":true");

			var slotM = System.Text.RegularExpressions.Regex.Match(o, "\"slot\":(\\d+)");
			if (slotM.Success) s.Slot = int.Parse(slotM.Groups[1].Value);

			var relM = System.Text.RegularExpressions.Regex.Match(o, "\"(rel|at)\":\\[(-?\\d+),(-?\\d+)\\]");
			if (relM.Success)
			{
				s.HasCursor = true;
				s.CursorFrozen = relM.Groups[1].Value == "at";
				s.CurDx = int.Parse(relM.Groups[2].Value);
				s.CurDy = int.Parse(relM.Groups[3].Value);
			}

			var uf = System.Text.RegularExpressions.Regex.Match(o, "\"until\":\\{\"frames\":(\\d+)");
			var ut = System.Text.RegularExpressions.Regex.Match(o, "\"until\":\\{\"times\":(\\d+)");
			bool upPlaced = System.Text.RegularExpressions.Regex.IsMatch(o, "\"until\":\\{\"placed\":true");
			var uc = System.Text.RegularExpressions.Regex.Match(o, "\"consumed\":\\{\"item\":(\\d+),\"n\":(\\d+)\\}");
			// moved: dx and dy are INDEPENDENTLY optional — {"moved":{"dy":-1}} is the natural way to say "one cell up",
			// and demanding both would reject it for a reason the caller cannot see.
			var umAny = System.Text.RegularExpressions.Regex.Match(o, "\"moved\":\\{([^}]*)\\}");
			var umDx = umAny.Success ? System.Text.RegularExpressions.Regex.Match(umAny.Groups[1].Value, "\"dx\":(-?\\d+)") : System.Text.RegularExpressions.Match.Empty;
			var umDy = umAny.Success ? System.Text.RegularExpressions.Regex.Match(umAny.Groups[1].Value, "\"dy\":(-?\\d+)") : System.Text.RegularExpressions.Match.Empty;
			var uy = System.Text.RegularExpressions.Regex.Match(o, "\"tile\":\\{\"rel\":\\[(-?\\d+),(-?\\d+)\\],\"has\":(true|false)\\}");
			if (upPlaced) { s.UntilKind = "placed"; }
			else if (uf.Success) { s.UntilKind = "frames"; s.UntilN = int.Parse(uf.Groups[1].Value); }
			else if (ut.Success) { s.UntilKind = "times"; s.UntilN = int.Parse(ut.Groups[1].Value); }
			else if (uc.Success) { s.UntilKind = "consumed"; s.UntilItemType = int.Parse(uc.Groups[1].Value); s.UntilN = int.Parse(uc.Groups[2].Value); }
			else if (umAny.Success)
			{
				s.UntilKind = "moved";
				s.UntilDx = umDx.Success ? int.Parse(umDx.Groups[1].Value) : 0;
				s.UntilDy = umDy.Success ? int.Parse(umDy.Groups[1].Value) : 0;
			}
			else if (uy.Success) { s.UntilKind = "tile"; s.UntilDx = int.Parse(uy.Groups[1].Value); s.UntilDy = int.Parse(uy.Groups[2].Value); s.UntilTileHas = uy.Groups[3].Value == "true"; }

			var inv = System.Text.RegularExpressions.Regex.Match(o, "\"invariant\":\\{\"(on_rope|cursor_in_reach|on_ground)\":(true|false)\\}");
			if (inv.Success) { s.InvKind = inv.Groups[1].Value; s.InvWant = inv.Groups[2].Value == "true"; }
			return s;
		}

		// 背包空槽数。背包满时游戏不把任何配方算作 available,available_count=0 看着像"没材料",
		// 所以合成失败一律把这个数报出来。
		static int FreeSlots()
		{
			var pf = Main.LocalPlayer;
			if (pf == null) return 0;
			int n = 0;
			for (int i = 0; i < 50; i++)
				if (pf.inventory[i] == null || pf.inventory[i].IsAir) n++;
			return n;
		}

		public static string JsonEscPublic(string s) => JsonEsc(s);

		static string JsonEsc(string s) =>
			s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

		static string JsonUnesc(string s) =>
			s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");

		// Vanilla tooltip text for an item, lines joined by " | ". This is what the mouseover box shows — the only
		// reliable way to tell same-named / confusable items apart (BOMB=summon vs 炸弹=destroy tiles).
		static string ItemTooltipText(Item it)
		{
			try
			{
				var tt = it.ToolTip ?? Lang.GetTooltip(it.type);
				if (tt == null || tt.Lines <= 0) return "";
				var parts = new System.Collections.Generic.List<string>();
				for (int i = 0; i < tt.Lines; i++)
				{
					string ln = tt.GetLine(i);
					if (!string.IsNullOrWhiteSpace(ln)) parts.Add(ln);
				}
				return string.Join(" | ", parts);
			}
			catch { return ""; }
		}

		// 箱子子类型名。frameX/36 就是子 id(金箱=1,常春藤=21…),名字查 Lang.chestType[]/chestType2[]。
		// 别用 MapHelper.TileToLookup:它的 option 是地图【颜色】分组,多种箱子共用一色 → 名字全错。
		// 陷阱箱:和普通箱子是不同的 TileID(BasicChestFake = 441/468),外观一模一样。
		// 箱子占 2x2,四个格都查一遍,点哪个角都认得出来。
		static bool IsFakeChest(int x, int y)
		{
			for (int dx = 0; dx <= 1; dx++)
				for (int dy = 0; dy <= 1; dy++)
				{
					int ax = x - dx, ay = y - dy;
					if (!Predicates.InBounds(ax, ay)) continue;
					var t = Main.tile[ax, ay];
					if (!t.HasTile) continue;
					if (t.TileType == Terraria.ID.TileID.FakeContainers
						|| t.TileType == Terraria.ID.TileID.FakeContainers2) return true;
				}
			return false;
		}

		// 蜂巢墙(HiveUnsafe=86 自然生成 / Hive=108 玩家放的)。宝藏贴在墙上,自己那格
		// 可能没墙,所以连邻格一起看。
		static bool InHive(int x, int y)
		{
			for (int dx = -1; dx <= 1; dx++)
				for (int dy = -1; dy <= 1; dy++)
				{
					int ax = x + dx, ay = y + dy;
					if (!Predicates.InBounds(ax, ay)) continue;
					int w = Main.tile[ax, ay].WallType;
					if (w == Terraria.ID.WallID.HiveUnsafe || w == Terraria.ID.WallID.Hive) return true;
				}
			return false;
		}

		static string ChestKindName(int tileType, int style)
		{
			try
			{
				var tbl = tileType == Terraria.ID.TileID.Containers2 ? Lang.chestType2 : Lang.chestType;
				if (style >= 0 && style < tbl.Length)
				{
					var name = tbl[style]?.Value;
					if (!string.IsNullOrEmpty(name)) return name;
				}
			}
			catch { }
			return "Chest";
		}
	}
}
