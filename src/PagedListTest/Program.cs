using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Open3270;
using Open3270.TN3270;

namespace PagedListTest
{
	/// <summary>
	/// Offline checks for paging through a list with PF8, the way a scrape does. No mainframe.
	///
	/// Page 1 has three entries, page 2 has one. Two different things made the scrape read page 2
	/// with page 1's last two entries still under its first:
	///
	///  1. The host blanks unused rows by re-sending their attribute as non-display, leaving
	///     page 1's characters in the buffer. A terminal draws them blank; the rendered screen
	///     used to show them.
	///  2. The host sends page 2 as two writes. The read after the first one sees a half updated
	///     screen, and WaitForHostSettle returned at the first arrival rather than the last.
	/// </summary>
	class Program
	{
		static int failures;

		static void Check(bool condition, string what)
		{
			Console.WriteLine((condition ? "  pass  " : "  FAIL  ") + what);
			if (!condition)
			{
				failures++;
			}
		}

		const int FirstListRow = 10;
		const byte Protected = 0x60;
		const byte ProtectedNonDisplay = 0x6C;

		static int Main()
		{
			Console.WriteLine("---- 1. non-display rows render blank ----");
			NonDisplay(false);
			NonDisplay(true);
			RevealOptIn();

			Console.WriteLine();
			Console.WriteLine("---- 2. WaitForHostSettle waits for the last write, not the first ----");
			Settle();

			Console.WriteLine();
			Console.WriteLine(failures == 0 ? "all checks passed" : failures + " check(s) failed");
			return failures == 0 ? 0 : 1;
		}

		#region 1. non-display

		/// <summary>Page 2, blanking rows 11-12 by turning them dark rather than erasing them.</summary>
		static byte[] Page2Dark()
		{
			List<byte> s = new List<byte>();
			s.Add(0xF1);   // Write - no erase, so page 1 is still in the buffer underneath
			s.Add(0xC3);
			Sba(s, Address(FirstListRow, 1));
			Text(s, "PAGE2 ENTRY 1 BBBBBBBBBB");
			Sba(s, Address(FirstListRow + 1, 0));
			Sf(s, ProtectedNonDisplay);
			Sba(s, Address(FirstListRow + 2, 0));
			Sf(s, ProtectedNonDisplay);
			return s.ToArray();
		}

		static IXMLScreen ReplayToPage2(TNEmulator emu, bool legacy, bool reveal, string dir)
		{
			string path = Path.Combine(dir, "dark-" + legacy + "-" + reveal + ".log");
			WriteLogFile(path, Page1(), Page2Dark());

			emu.Config.LogFile = new StreamReader(path);
			emu.Config.ThrowExceptionOnLockedScreen = false;
			emu.Config.UseLegacyXmlScreenRendering = legacy;
			emu.Config.RevealNonDisplayFields = reveal;
			emu.Connect();

			// The replay feeds every record at once; wait for page 2 to land.
			DateTime deadline = DateTime.Now.AddSeconds(3);
			while (DateTime.Now < deadline)
			{
				emu.Refresh();
				if (emu.CurrentScreenXML != null && emu.CurrentScreenXML.GetText(1, FirstListRow, 5) == "PAGE2")
				{
					break;
				}
				Thread.Sleep(20);
			}
			return emu.CurrentScreenXML;
		}

		static void NonDisplay(bool legacy)
		{
			string label = legacy ? "[legacy] " : "[direct] ";
			string dir = TempDir();
			try
			{
				using (TNEmulator emu = new TNEmulator())
				{
					IXMLScreen screen = ReplayToPage2(emu, legacy, false, dir);
					Check(screen != null && screen.GetText(1, FirstListRow, 13) == "PAGE2 ENTRY 1",
						label + "page 2's entry is on row " + FirstListRow);
					if (screen == null)
					{
						return;
					}

					string row11 = screen.GetRow(FirstListRow + 1);
					string row12 = screen.GetRow(FirstListRow + 2);
					Check(row11.Trim().Length == 0 && row12.Trim().Length == 0,
						label + "the dark rows render blank, as a terminal shows them (got '"
						+ row11.Trim() + "', '" + row12.Trim() + "')");
					Check(screen.LookForTextStrings(new string[] { "PAGE1" }) == -1,
						label + "LookForTextStrings does not find page 1's hidden text");

					// Nothing is lost: the field still carries the raw text and says it is hidden.
					bool sawHidden = false;
					foreach (XMLScreenField field in screen.Fields)
					{
						if (field.Location.top == FirstListRow + 1 && field.Attributes.FieldType == "Hidden"
							&& field.Text != null && field.Text.StartsWith("PAGE1 ENTRY 2"))
						{
							sawHidden = true;
						}
					}
					Check(sawHidden, label + "Fields still carries the hidden text, with FieldType Hidden");
				}
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		static void RevealOptIn()
		{
			string dir = TempDir();
			try
			{
				using (TNEmulator emu = new TNEmulator())
				{
					IXMLScreen screen = ReplayToPage2(emu, false, true, dir);
					Check(screen != null && screen.GetText(1, FirstListRow + 1, 13) == "PAGE1 ENTRY 2",
						"RevealNonDisplayFields = true renders the hidden text, as before");
				}
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		#endregion

		#region 2. settle

		/// <summary>
		/// A host on a local socket. It sends page 1, waits for the PF8, then answers with page 2 as
		/// two writes 100 ms apart: the new entry first, the stale rows blanked second. Both writes
		/// restore the keyboard, so only the quiet period can tell the first from the last - which
		/// means the settle interval has to be longer than the gap between the host's writes.
		/// </summary>
		static void Settle()
		{
			TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			int port = ((IPEndPoint)listener.LocalEndpoint).Port;

			Thread host = new Thread(delegate ()
			{
				try
				{
					using (TcpClient client = listener.AcceptTcpClient())
					{
						NetworkStream net = client.GetStream();
						Send(net, Negotiation());
						Thread.Sleep(300);
						Send(net, Record(Page1()));

						// Skip the negotiation replies and wait for the PF8 AID. 0xF8 cannot
						// appear in what the client sends during negotiation.
						WaitForAid(net, 0xF8);

						List<byte> first = new List<byte> { 0xF1, 0xC3 };
						Sba(first, Address(FirstListRow, 1));
						Text(first, "PAGE2 ENTRY 1 BBBBBBBBBB");
						Send(net, Record(first.ToArray()));

						Thread.Sleep(100);

						List<byte> second = new List<byte> { 0xF1, 0xC3 };
						Sba(second, Address(FirstListRow + 1, 1));
						Text(second, new string(' ', 30));
						Sba(second, Address(FirstListRow + 2, 1));
						Text(second, new string(' ', 30));
						Send(net, Record(second.ToArray()));

						Thread.Sleep(3000);
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("  host: " + e.Message);
				}
			});
			host.IsBackground = true;
			host.Start();

			using (TNEmulator emu = new TNEmulator())
			{
				emu.Config.ThrowExceptionOnLockedScreen = false;
				emu.Connect("127.0.0.1", port, null);
				Check(emu.CurrentScreenXML.GetText(1, FirstListRow + 2, 13) == "PAGE1 ENTRY 3", "page 1 has three entries");

				emu.SendKey(true, TnKey.F8, 5000);
				string afterSendKey = emu.CurrentScreenXML.GetText(1, FirstListRow + 1, 13);
				Console.WriteLine("        after SendKey alone, row " + (FirstListRow + 1) + " reads '" + afterSendKey
					+ "' - SendKey returns at the first write, which is why a settle is needed");

				DateTime start = DateTime.Now;
				bool settled = emu.WaitForHostSettle(300, 5000);
				double ms = (DateTime.Now - start).TotalMilliseconds;

				Check(settled, "WaitForHostSettle reports settled");
				Check(ms >= 300 && ms < 2000, "it waited through the gap before the second write, then stopped (" + (int)ms + " ms)");
				IXMLScreen screen = emu.CurrentScreenXML;
				Check(screen.GetText(1, FirstListRow, 13) == "PAGE2 ENTRY 1", "page 2's entry is there");
				Check(screen.GetRow(FirstListRow + 1).Trim().Length == 0 && screen.GetRow(FirstListRow + 2).Trim().Length == 0,
					"page 1's leftover entries are gone");

				start = DateTime.Now;
				settled = emu.WaitForHostSettle(500, 200);
				Check(!settled, "a final timeout shorter than the interval reports not settled");

				start = DateTime.Now;
				settled = emu.WaitForHostSettle(300, 5000);
				ms = (DateTime.Now - start).TotalMilliseconds;
				Check(settled && ms < 1000, "an already quiet host settles after one interval (" + (int)ms + " ms)");
			}

			listener.Stop();
		}

		static byte[] Negotiation()
		{
			return new byte[]
			{
				0xFF, 0xFD, 0x18,               // DO TERMINAL-TYPE
				0xFF, 0xFA, 0x18, 0x01, 0xFF, 0xF0, // SB TERMINAL-TYPE SEND SE
				0xFF, 0xFD, 0x19, 0xFF, 0xFB, 0x19, // DO / WILL END-OF-RECORD
				0xFF, 0xFD, 0x00, 0xFF, 0xFB, 0x00, // DO / WILL BINARY
			};
		}

		static byte[] Record(byte[] data)
		{
			byte[] r = new byte[data.Length + 2];
			data.CopyTo(r, 0);
			r[data.Length] = 0xFF;   // IAC EOR
			r[data.Length + 1] = 0xEF;
			return r;
		}

		static void Send(NetworkStream net, byte[] data)
		{
			net.Write(data, 0, data.Length);
			net.Flush();
		}

		static void WaitForAid(NetworkStream net, byte aid)
		{
			net.ReadTimeout = 10000;
			byte[] buf = new byte[4096];
			while (true)
			{
				int n = net.Read(buf, 0, buf.Length);
				if (n <= 0)
				{
					throw new IOException("client closed before sending the AID");
				}
				if (Array.IndexOf(buf, aid, 0, n) >= 0)
				{
					return;
				}
			}
		}

		#endregion

		#region building screens without a mainframe

		static byte[] Page1()
		{
			List<byte> s = new List<byte>();
			s.Add(0xF5);   // Erase/Write
			s.Add(0xC3);
			Sba(s, 0);
			Sf(s, Protected);
			Text(s, "TRANSACTION HISTORY");
			for (int i = 0; i < 3; i++)
			{
				Sba(s, Address(FirstListRow + i, 0));
				Sf(s, Protected);
				Text(s, "PAGE1 ENTRY " + (i + 1) + " AAAAAAAAAA");
				Sba(s, Address(FirstListRow + i, 40));
				Sf(s, Protected);
			}
			Sba(s, Address(23, 0));
			Sf(s, Protected);
			Text(s, "PF8 NEXT PAGE");
			return s.ToArray();
		}

		static int Address(int row, int column)
		{
			return row * 80 + column;
		}

		// EBCDIC code table used to encode 12-bit buffer addresses (ControllerConstant.CodeTable)
		static readonly byte[] CodeTable = new byte[]
		{
			0x40, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7,
			0xC8, 0xC9, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
			0x50, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7,
			0xD8, 0xD9, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
			0x60, 0x61, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7,
			0xE8, 0xE9, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
			0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7,
			0xF8, 0xF9, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
		};

		static byte Ebcdic(char c)
		{
			if (c >= 'A' && c <= 'I') return (byte)(0xC1 + (c - 'A'));
			if (c >= 'J' && c <= 'R') return (byte)(0xD1 + (c - 'J'));
			if (c >= 'S' && c <= 'Z') return (byte)(0xE2 + (c - 'S'));
			if (c >= '0' && c <= '9') return (byte)(0xF0 + (c - '0'));
			return 0x40;   // space, and anything this test does not need
		}

		static void Sba(List<byte> s, int addr)
		{
			s.Add(0x11);
			s.Add(CodeTable[(addr >> 6) & 0x3F]);
			s.Add(CodeTable[addr & 0x3F]);
		}

		static void Sf(List<byte> s, byte fa)
		{
			s.Add(0x1D);
			s.Add(fa);
		}

		static void Text(List<byte> s, string text)
		{
			foreach (char c in text)
			{
				s.Add(Ebcdic(c));
			}
		}

		static string TempDir()
		{
			string dir = Path.Combine(Path.GetTempPath(), "open3270-pagedlist-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			return dir;
		}

		/// <summary>Builds a replay log the library's Config.LogFile parser understands.</summary>
		static void WriteLogFile(string path, params byte[][] records)
		{
			List<string> lines = new List<string>();
			lines.Add("FF FD 18");
			lines.Add("FF FA 18 01 FF F0");
			lines.Add("FF FD 19");
			lines.Add("FF FB 19");
			lines.Add("FF FD 00");
			lines.Add("FF FB 00");
			foreach (byte[] record in records)
			{
				StringBuilder data = new StringBuilder();
				foreach (byte b in record)
				{
					data.Append(b.ToString("X2")).Append(' ');
				}
				data.Append("FF EF");
				lines.Add(data.ToString());
			}

			using (StreamWriter w = new StreamWriter(path, false))
			{
				int t = 1;
				foreach (string line in lines)
				{
					w.WriteLine(t.ToString("D6") + "   " + "H " + "       " + line);
					t++;
				}
			}
		}

		#endregion
	}
}
