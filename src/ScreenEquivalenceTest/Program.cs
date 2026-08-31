using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Open3270;

namespace ScreenEquivalenceTest
{

	/// <summary>Counts how many times the emulator re-fetched the screen.</summary>
	class CountingAudit : IAudit
	{
		public int Reloads;
		public void Write(string text) { }
		public void WriteLine(string text)
		{
			if (text != null && text.StartsWith("CurrentScreenXML reloading"))
			{
				Reloads++;
			}
		}
	}

	class Program
	{
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

		// SFE with a basic attribute plus extended attribute pairs
		static void Sfe(List<byte> s, byte fa, params byte[] pairs)
		{
			s.Add(0x29);
			s.Add((byte)(1 + pairs.Length / 2));
			s.Add(0xC0); s.Add(fa);
			s.AddRange(pairs);
		}

		static void Text(List<byte> s, string ascii)
		{
			foreach (char c in ascii)
			{
				s.Add(AsciiToEbcdic(c));
			}
		}

		static byte AsciiToEbcdic(char c)
		{
			if (c >= 'A' && c <= 'I') return (byte)(0xC1 + (c - 'A'));
			if (c >= 'J' && c <= 'R') return (byte)(0xD1 + (c - 'J'));
			if (c >= 'S' && c <= 'Z') return (byte)(0xE2 + (c - 'S'));
			if (c >= 'a' && c <= 'i') return (byte)(0x81 + (c - 'a'));
			if (c >= 'j' && c <= 'r') return (byte)(0x91 + (c - 'j'));
			if (c >= 's' && c <= 'z') return (byte)(0xA2 + (c - 's'));
			if (c >= '0' && c <= '9') return (byte)(0xF0 + (c - '0'));
			switch (c)
			{
				case ' ': return 0x40;
				case '<': return 0x4C;
				case '(': return 0x4D;
				case '+': return 0x4E;
				case '&': return 0x50;
				case '$': return 0x5B;
				case '*': return 0x5C;
				case ')': return 0x5D;
				case '-': return 0x60;
				case '/': return 0x61;
				case ',': return 0x6B;
				case '.': return 0x4B;
				case '>': return 0x6E;
				case '?': return 0x6F;
				case ':': return 0x7A;
				case '=': return 0x7E;
				case '\'': return 0x7D;
				default: return 0x40;
			}
		}

		/// <summary>Builds a replay log the library's Config.LogFile parser understands.</summary>
		static string WriteLogFile(string path, List<byte> screenStream)
		{
			List<string> records = new List<string>();

			// Telnet negotiation to get the session into 3270 mode
			records.Add("FF FD 18");                 // DO TERMINAL-TYPE
			records.Add("FF FA 18 01 FF F0");        // SB TERMINAL-TYPE SEND SE
			records.Add("FF FD 19");                 // DO END-OF-RECORD
			records.Add("FF FB 19");                 // WILL END-OF-RECORD
			records.Add("FF FD 00");                 // DO BINARY
			records.Add("FF FB 00");                 // WILL BINARY

			StringBuilder data = new StringBuilder();
			foreach (byte b in screenStream)
			{
				data.Append(b.ToString("X2")).Append(' ');
			}
			data.Append("FF EF");                    // IAC EOR terminates the 3270 record
			records.Add(data.ToString());

			using (StreamWriter w = new StreamWriter(path, false))
			{
				int t = 1;
				foreach (string r in records)
				{
					// cols 0-5 time, col 9-10 direction, hex from col 18
					// cols 0-5 time, cols 9-10 "H ", hex from col 18
					w.WriteLine(t.ToString("D6") + "   " + "H " + "       " + r);
					t++;
				}
			}
			return path;
		}

		static List<byte> BuildScreen(bool includeMarkupCharacters)
		{
			List<byte> s = new List<byte>();
			s.Add(0xF5);  // Erase/Write
			s.Add(0xC3);  // WCC: reset, restore keyboard, reset MDT

			// row 0: protected, intensified
			Sba(s, 0);
			Sf(s, 0xE8);
			Text(s, "ACCOUNT INQUIRY");

			// row 2: protected label then an unprotected input field
			Sba(s, 160);
			Sf(s, 0x60);
			Text(s, "ACCOUNT NUMBER:");
			Sba(s, 177);
			Sf(s, 0x00);
			Text(s, "1234567890");

			// row 4: hidden (non-display) field
			Sba(s, 320);
			Sf(s, 0x6C);
			Text(s, "SECRET");

			// row 6: extended attributes - red on blue
			Sba(s, 480);
			Sfe(s, 0x60, 0x42, 0xF2, 0x45, 0xF1);
			Text(s, "COLOURED TEXT");

			// row 8: a field that runs off the end of its row, to exercise row splitting
			Sba(s, 640);
			Sf(s, 0x60);
			Text(s, new string('X', 100));

			if (includeMarkupCharacters)
			{
				// row 12: characters that the XML dump has to escape
				Sba(s, 960);
				Sf(s, 0x60);
				Text(s, "A<B&C>D");
			}

			return s;
		}

		static string Capture(IXMLScreen screen)
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("CX=" + screen.CX + " CY=" + screen.CY);
			sb.AppendLine("--- dump ---");
			sb.AppendLine(screen.Dump());

			sb.AppendLine("--- unformatted ---");
			string[] rows = screen.GetUnformatedStrings();
			if (rows == null)
			{
				sb.AppendLine("(null)");
			}
			else
			{
				for (int i = 0; i < rows.Length; i++)
				{
					sb.AppendLine(i.ToString("D2") + "[" + rows[i] + "]");
				}
			}

			sb.AppendLine("--- fields ---");
			Open3270.TN3270.XMLScreenField[] fields = screen.Fields;
			if (fields == null)
			{
				sb.AppendLine("(null)");
			}
			else
			{
				sb.AppendLine("count=" + fields.Length);
				foreach (Open3270.TN3270.XMLScreenField f in fields)
				{
					sb.Append("pos=" + f.Location.position);
					sb.Append(" left=" + f.Location.left);
					sb.Append(" top=" + f.Location.top);
					sb.Append(" len=" + f.Location.length);
					sb.Append(" base=" + f.Attributes.Base);
					sb.Append(" prot=" + f.Attributes.Protected);
					sb.Append(" type=" + (f.Attributes.FieldType ?? "-"));
					sb.Append(" fg=" + (f.Attributes.Foreground ?? "-"));
					sb.Append(" bg=" + (f.Attributes.Background ?? "-"));
					sb.AppendLine(" text=[" + f.Text + "]");
				}
			}
			return sb.ToString();
		}

		static string Run(string logPath, bool legacy)
		{
			using (TNEmulator emu = new TNEmulator())
			{
				emu.Config.LogFile = new StreamReader(logPath);
				emu.Config.UseLegacyXmlScreenRendering = legacy;
				emu.Config.ThrowExceptionOnLockedScreen = false;
				emu.Connect();
				emu.Refresh(true, 2000);
				IXMLScreen screen = emu.CurrentScreenXML;
				if (screen == null)
				{
					return "(null screen)";
				}
				string result = Capture(screen);
				emu.Close();
				return result;
			}
		}


		static void WaitTests(string logPath, bool alwaysRefresh)
		{
			Console.WriteLine("---- wait loops (AlwaysRefreshWhenWaiting=" + alwaysRefresh + ") ----");
			using (TNEmulator emu = new TNEmulator())
			{
				CountingAudit audit = new CountingAudit();
				emu.Config.LogFile = new StreamReader(logPath);
				emu.Config.ThrowExceptionOnLockedScreen = false;
				emu.Config.AlwaysRefreshWhenWaiting = alwaysRefresh;
				emu.Connect();
				emu.Refresh(true, 2000);

				emu.Audit = audit;
				emu.Debug = true;

				Time(emu, audit, "hit  WaitForTextOnScreen(2000,'ACCOUNT INQUIRY')",
					delegate { return emu.WaitForTextOnScreen(2000, "ACCOUNT INQUIRY").ToString(); });

				Time(emu, audit, "miss WaitForTextOnScreen(1500,'NOT ON SCREEN')",
					delegate { return emu.WaitForTextOnScreen(1500, "NOT ON SCREEN").ToString(); });

				Time(emu, audit, "hit  WaitForText(1,0,'ACCOUNT INQUIRY',2000)",
					delegate { return emu.WaitForText(1, 0, "ACCOUNT INQUIRY", 2000).ToString(); });

				Time(emu, audit, "miss WaitForText(1,0,'NOPE',1500)",
					delegate { return emu.WaitForText(1, 0, "NOPE", 1500).ToString(); });

				emu.Close();
			}
			Console.WriteLine();
		}

		delegate string TimedCall();

		static void Time(TNEmulator emu, CountingAudit audit, string label, TimedCall call)
		{
			audit.Reloads = 0;
			DateTime t0 = DateTime.Now;
			string result = call();
			int ms = (int)(DateTime.Now - t0).TotalMilliseconds;
			Console.WriteLine(String.Format("{0,-48} -> {1,-6} {2,6} ms  {3} screen fetch(es)",
				label, result, ms, audit.Reloads));
		}

		static int Main(string[] args)
		{
			string dir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
			int failures = 0;

			foreach (bool markup in new bool[] { false, true })
			{
				string label = markup ? "screen with < & > characters" : "plain screen";
				string logPath = Path.Combine(dir, markup ? "markup.log" : "plain.log");
				WriteLogFile(logPath, BuildScreen(markup));

				string direct = Run(logPath, false);
				string legacy = Run(logPath, true);

				Console.WriteLine("================ " + label + " ================");
				if (direct == legacy)
				{
					Console.WriteLine("IDENTICAL");
				}
				else
				{
					Console.WriteLine("DIFFERENT");
					failures++;
					string[] a = direct.Replace("\r\n", "\n").Split('\n');
					string[] b = legacy.Replace("\r\n", "\n").Split('\n');
					for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
					{
						string la = i < a.Length ? a[i] : "(missing)";
						string lb = i < b.Length ? b[i] : "(missing)";
						if (la != lb)
						{
							Console.WriteLine("  line " + i);
							Console.WriteLine("    direct: " + la);
							Console.WriteLine("    legacy: " + lb);
						}
					}
				}
				File.WriteAllText(Path.Combine(dir, (markup ? "markup" : "plain") + ".direct.txt"), direct);
				File.WriteAllText(Path.Combine(dir, (markup ? "markup" : "plain") + ".legacy.txt"), legacy);
			}

			Console.WriteLine();

			string waitLog = Path.Combine(dir, "plain.log");
			WaitTests(waitLog, false);
			WaitTests(waitLog, true);

			Console.WriteLine(failures == 0 ? "ALL SCREENS MATCH" : failures + " screen(s) differ");
			return failures == 0 ? 0 : 1;
		}
	}
}
