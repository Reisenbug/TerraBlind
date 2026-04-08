using System;
using System.Net;
using System.Text;
using System.Threading;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class HttpServerSystem : ModSystem
	{
		public static volatile Snapshot LatestSnapshot;

		private const string Prefix = "http://127.0.0.1:17878/";
		private HttpListener _listener;
		private Thread _thread;
		private volatile bool _running;
		private bool _announced;

		public override void PostUpdateEverything()
		{
			if (_announced || !_running) return;
			if (Main.netMode == 2) { _announced = true; return; }
			if (Main.LocalPlayer == null || !Main.LocalPlayer.active) return;
			Main.NewText("[TerraBlind] HTTP server listening on " + Prefix, Color.LightGreen);
			_announced = true;
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
