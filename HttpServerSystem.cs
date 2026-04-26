using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class ControlInput
	{
		public bool Left, Right, Up, Down, Jump, UseItem;
		public int SelectedSlot = -1;
		public long Tick;
	}

	public class HttpServerSystem : ModSystem
	{
		public static volatile Snapshot LatestSnapshot;
		public static volatile ControlInput PendingControl;

		private static readonly ConcurrentQueue<(int src, int dst)> _swapQueue = new();
		private static readonly ConcurrentQueue<(int tx, int ty)> _interactQueue = new();
		private static volatile bool _lootAllRequested;
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
			while (_swapQueue.TryDequeue(out var swap))
			{
				int src = swap.src, dst = swap.dst;
				if (src < 0 || src > 57 || dst < 0 || dst > 57 || src == dst) continue;
				var inv = Main.LocalPlayer.inventory;
				(inv[src], inv[dst]) = (inv[dst], inv[src]);
			}
			while (_interactQueue.TryDequeue(out var tile))
			{
				if (Main.LocalPlayer.chest != -1) continue;
				int idx = Chest.FindChest(tile.tx, tile.ty);
				if (idx == -1) continue;
				if (Chest.UsingChest(idx) != -1) continue;
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
					Terraria.UI.ChestUI.LootAll();
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

		private void Handle(HttpListenerContext ctx)
		{
			string path = ctx.Request.Url.AbsolutePath;
			string body;
			int status = 200;

			if (path == "/state")
			{
				body = StateSerializer.ToJson(LatestSnapshot);
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
				if (rb.Contains("\"use_item\":true")) ci.UseItem = true;
				var slotMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"selected_slot\"\\s*:\\s*(\\d+)");
				if (slotMatch.Success) ci.SelectedSlot = int.Parse(slotMatch.Groups[1].Value);
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
					_interactQueue.Enqueue((tx, ty));
					body = "{\"ok\":true,\"tile_x\":" + tx + ",\"tile_y\":" + ty + "}";
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
					else if (skillName == "cave_bypass")
					{
						var riseMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"rise_tiles\"\\s*:\\s*(\\d+)");
						var walkMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"walk_back\"\\s*:\\s*(\\d+)");
						int riseTiles = riseMatch.Success ? int.Parse(riseMatch.Groups[1].Value) : 5;
						int walkBack = walkMatch.Success ? int.Parse(walkMatch.Groups[1].Value) : 2;
						bool caveOnLeft = dirMatch.Success && dirMatch.Groups[1].Value == "left";
						SkillExecutor.StartCaveBypass(caveOnLeft, walkBack, riseTiles);
						body = "{\"ok\":true,\"skill\":\"cave_bypass\"}";
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
					frames.Add(new ReplayFrame
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
						Mx           = mxm.Success ? float.Parse(mxm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0f,
						My           = mym.Success ? float.Parse(mym.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0f,
					});
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
			else if (path == "/health")
			{
				body = "{\"ok\":true}";
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
	}
}
