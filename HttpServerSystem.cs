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
		public bool SmartCursor;
		public long Tick;
	}

	public class HttpServerSystem : ModSystem
	{
		public static volatile Snapshot LatestSnapshot;
		public static volatile ControlInput PendingControl;

		private static readonly ConcurrentQueue<(int src, int dst)> _swapQueue = new();
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
				if (rb.Contains("\"smart_cursor\":true")) ci.SmartCursor = true;
				var slotMatch = System.Text.RegularExpressions.Regex.Match(rb, "\"selected_slot\"\\s*:\\s*(\\d+)");
				if (slotMatch.Success) ci.SelectedSlot = int.Parse(slotMatch.Groups[1].Value);
				ci.Tick = (long)Main.GameUpdateCount;
				PendingControl = ci;
				body = "{\"ok\":true}";
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
