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
			else if (path == "/mine")
			{
				string reqBody;
				using (var sr = new System.IO.StreamReader(ctx.Request.InputStream))
					reqBody = sr.ReadToEnd();
				var tiles = new System.Collections.Generic.List<(int, int)>();
				var pairMatches = System.Text.RegularExpressions.Regex.Matches(
					reqBody.Replace(" ", ""),
					"\\{\"wx\"\\s*:\\s*(-?\\d+),\"wy\"\\s*:\\s*(-?\\d+)\\}");
				foreach (System.Text.RegularExpressions.Match m in pairMatches)
					tiles.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
				if (tiles.Count > 0)
				{
					MineCoordinator.Start(new MineRequest { Tiles = tiles });
					body = "{\"ok\":true}";
				}
				else
				{
					body = "{\"error\":\"bad_params\",\"usage\":\"POST /mine {\\\"tiles\\\":[{\\\"wx\\\":N,\\\"wy\\\":N}]}\"}";
					status = 400;
				}
			}
			else if (path == "/mine_stop")
			{
				MineCoordinator.Stop();
				body = "{\"ok\":true}";
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
					string targetName = nameMatch.Groups[1].Value.ToLowerInvariant();
					for (int ri = 0; ri < Main.numAvailableRecipes; ri++)
					{
						var r = Main.recipe[Main.availableRecipe[ri]];
						if ((r.createItem.Name ?? "").ToLowerInvariant() == targetName)
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
					sb2.Append(",\"raw_name\":\"").Append(nameMatch.Success ? nameMatch.Groups[1].Value : "").Append("\"");
					sb2.Append(",\"available_names\":[");
					for (int ri = 0; ri < Main.numAvailableRecipes; ri++)
					{
						if (ri > 0) sb2.Append(',');
						sb2.Append('"').Append(Main.recipe[Main.availableRecipe[ri]].createItem.Name ?? "").Append('"');
					}
					sb2.Append("]}");
					body = sb2.ToString();
					status = 400;
				}
				else
				{
					int crafted = CraftCoordinator.Craft(targetId, amount);
					if (crafted > 0)
						body = "{\"ok\":true,\"crafted\":" + crafted + "}";
					else
						body = "{\"error\":\"not_available\",\"item_id\":" + targetId + "}";
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
				NavCoordinator.Start(navSign);
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
				NavCoordinator.Stop();
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
				if (NavCoordinator.Done)
					body = "{\"done\":true,\"status\":\"done\"}";
				else if (!NavCoordinator.IsActive && !NavCoordinator.Done)
					body = "{\"done\":false,\"status\":\"failed\",\"reason\":\"" + NavCoordinator.FailReason + "\"}";
				else
					body = "{\"done\":false,\"status\":\"running\"}";
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
						if (Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType]) sb2.Append('#');
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
