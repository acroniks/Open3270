using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Open3270;

namespace ScreenReadTest
{
	/// <summary>
	/// Offline checks on coordinate reads. No mainframe: a screen is built as a 3270 data stream,
	/// written as a replay log, and fed through the real state machine.
	///
	/// These exist because a session-open assertion and a recognizer anchor are both comparisons,
	/// and every way GetText(x, y, length) can go wrong makes a comparison go quiet rather than
	/// loud - it wraps into the next row, it returns a short string, or it reads a row the host
	/// never sent. The point of GetTextExact is that each of those fails instead.
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

		static int Main()
		{
			string dir = Path.Combine(Path.GetTempPath(), "open3270-screenread-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);

			try
			{
				string logPath = Path.Combine(dir, "screen.log");
				WriteLogFile(logPath, BuildScreen());

				using (TNEmulator emu = new TNEmulator())
				{
					emu.Config.LogFile = new StreamReader(logPath);
					emu.Config.ThrowExceptionOnLockedScreen = false;
					emu.Connect();
					emu.Refresh(true, 2000);

					IXMLScreen screen = emu.CurrentScreenXML;
					if (screen == null)
					{
						Console.WriteLine("no screen arrived - cannot run any check");
						return 1;
					}

					Console.WriteLine("negotiated geometry : " + emu.ScreenRows + " rows by "
						+ emu.ScreenColumns + " columns");
					Console.WriteLine("screen object says  : " + screen.CY + " rows by "
						+ screen.CX + " columns   <- floored, and that is the point of check 3");
					Console.WriteLine();

					string row0 = screen.GetRow(0);
					string row1 = screen.GetRow(1);

					Console.WriteLine("---- 1. a read inside one row ----");
					InsideOneRow(screen, row0);

					Console.WriteLine();
					Console.WriteLine("---- 2. a read that would leave its row ----");
					LeavingTheRow(screen, row0, row1);

					Console.WriteLine();
					Console.WriteLine("---- 3. a row the host never sent ----");
					PhantomRow(emu, screen);

					Console.WriteLine();
					Console.WriteLine("---- 4. reads that are not reads at all ----");
					Nonsense(screen);

					Console.WriteLine();
					Console.WriteLine("---- 5. the region assertion, in the shape phase 02 item 0 ships ----");
					RegionAssertion(emu, row0);

					emu.Close();
				}
			}
			finally
			{
				try { Directory.Delete(dir, true); } catch { }
			}

			Console.WriteLine();
			Console.WriteLine(failures == 0
				? "ALL CHECKS PASS"
				: failures + " CHECK(S) FAILED");
			return failures == 0 ? 0 : 1;
		}

		#region checks

		/// <summary>The ordinary case: exactly the characters asked for, from the row asked for.</summary>
		static void InsideOneRow(IXMLScreen screen, string row0)
		{
			int at = row0.IndexOf("4242", StringComparison.Ordinal);
			Check(at >= 0, "the region code is on row 0 (found at column " + at + ")");
			if (at < 0)
			{
				return;
			}

			Check(screen.GetTextExact(at, 0, 4) == "4242", "GetTextExact returns exactly the four characters");
			Check(screen.GetTextExact(at, 0, 0) == string.Empty, "a zero length read is the empty string, not a failure");

			string text;
			Check(screen.TryGetTextExact(at, 0, 4, out text) && text == "4242",
				"TryGetTextExact reports success and the same text");
		}

		/// <summary>
		/// The finding this whole file exists for. GetText resolves (x, y) to a flat offset, so a
		/// read running off the end of row 0 continues into row 1 and returns a string that never
		/// appeared anywhere on the screen.
		/// </summary>
		static void LeavingTheRow(IXMLScreen screen, string row0, string row1)
		{
			const int start = 76;
			const int length = 8;   // 4 characters left in row 0, so 4 would come from row 1

			string wrapped = screen.GetText(start, 0, length);
			string spanning = row0.Substring(start, 4) + row1.Substring(0, 4);

			Check(wrapped == spanning,
				"GetText wraps into the next row and returns \"" + Printable(wrapped)
				+ "\", which is row 0 plus row 1 - documented, not endorsed");

			string text;
			Check(!screen.TryGetTextExact(start, 0, length, out text),
				"TryGetTextExact refuses the same read");
			Check(text == null, "and hands back no text to compare against by accident");

			bool threw = false;
			try
			{
				screen.GetTextExact(start, 0, length);
			}
			catch (ArgumentOutOfRangeException e)
			{
				threw = true;
				Check(e.Message.Contains("row"), "GetTextExact throws and the message names the geometry");
			}
			Check(threw, "GetTextExact throws rather than returning something plausible");
		}

		/// <summary>
		/// IXMLScreen.CY is floored at 25, so on a 24 row model 2 the screen object believes row 24
		/// exists and reads it back as blanks. The emulator knows better, because it kept the
		/// negotiated geometry. An assertion reading blanks is an assertion that passes quietly.
		/// </summary>
		static void PhantomRow(TNEmulator emu, IXMLScreen screen)
		{
			int negotiated = emu.ScreenRows;
			Check(negotiated == 24, "the negotiated screen is 24 rows (model 2), got " + negotiated);
			Check(screen.CY == 25, "the screen object still reports 25 rows, floored, got " + screen.CY);

			string text;
			Check(screen.TryGetTextExact(0, 24, 4, out text),
				"the screen object allows row 24 - it cannot know better");
			Check(text != null && text.Trim().Length == 0,
				"and returns blanks, which is exactly what a quiet assertion failure looks like");

			Check(!emu.TryGetTextExact(0, 24, 4, out text),
				"the emulator refuses row 24, because the host never sent one");
			Check(emu.TryGetTextExact(0, 23, 4, out text),
				"and still allows row 23, the real last row");

			bool threw = false;
			try
			{
				emu.GetTextExact(0, 24, 4);
			}
			catch (ArgumentOutOfRangeException e)
			{
				threw = true;
				Check(e.Message.Contains("negotiated"),
					"the thrown message says the bound came from the negotiated geometry");
			}
			Check(threw, "emulator GetTextExact throws on a row the host never sent");
		}

		/// <summary>Negative coordinates crash the old overload. They should just be refused.</summary>
		static void Nonsense(IXMLScreen screen)
		{
			string text;
			Check(!screen.TryGetTextExact(-1, 0, 4, out text), "a negative column is refused");
			Check(!screen.TryGetTextExact(0, -1, 4, out text), "a negative row is refused");
			Check(!screen.TryGetTextExact(0, 0, -4, out text), "a negative length is refused");
			Check(!screen.TryGetTextExact(0, 0, 9999, out text), "an absurd length is refused");

			bool crashed = false;
			try
			{
				screen.GetText(-1, 0, 4);
			}
			catch (IndexOutOfRangeException)
			{
				crashed = true;
			}
			Check(crashed, "for contrast, the old GetText throws IndexOutOfRangeException on the same input");
		}

		/// <summary>
		/// What item 0 actually ships, reduced to its core: read the region code at pinned
		/// coordinates before any keystroke and compare it against the target's configuration.
		/// Both outcomes have to be loud - a mismatch, and a read that could not happen at all.
		/// </summary>
		static void RegionAssertion(TNEmulator emu, string row0)
		{
			int at = row0.IndexOf("4242", StringComparison.Ordinal);
			if (at < 0)
			{
				return;
			}

			Check(AssertRegion(emu, at, 0, "4242"), "the configured region matches, so the session opens");
			Check(!AssertRegion(emu, at, 0, "4243"), "a different region fails, which is the misroute this catches");
			Check(!AssertRegion(emu, at, 24, "4242"), "an unreadable position fails too, rather than passing on blanks");
		}

		static bool AssertRegion(TNEmulator emu, int x, int y, string expected)
		{
			string actual;
			if (!emu.TryGetTextExact(x, y, expected.Length, out actual))
			{
				return false;
			}
			return actual == expected;
		}

		static string Printable(string s)
		{
			return s == null ? "(null)" : s.Replace(' ', '.');
		}

		#endregion

		#region building a screen without a mainframe

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

		/// <summary>
		/// A 24 by 80 screen shaped like the one item 0 reads. The region code here is invented -
		/// this is a public repository, so the test uses a shape, never a real identifier.
		/// A region code on row 0, something
		/// different on row 1 so a wrapped read is visibly wrong, and text running to the right
		/// edge of row 0 so a read can be made to run off it.
		/// </summary>
		static byte[] BuildScreen()
		{
			List<byte> s = new List<byte>();
			s.Add(0xF5);   // Erase/Write
			s.Add(0xC3);   // WCC: reset, keyboard restore

			// row 0, from column 0: the field attribute sits at column 0, text from column 1
			Sba(s, 0);
			Sf(s, 0xE8);                       // protected, intensified
			Text(s, "REGION 4242 LOGIN");

			// row 0, out to the right edge, so a read from column 76 has somewhere to run off to
			Sba(s, 70);
			Sf(s, 0x60);                       // protected
			Text(s, "ZZZZZZZZZ");              // columns 71-79

			// row 1, so a wrapped read picks up something recognisably from the wrong row
			Sba(s, 80);
			Sf(s, 0x60);
			Text(s, "ACCOUNT NUMBER");

			s.Add(0xFF);   // IAC EOR terminates the 3270 record
			s.Add(0xEF);
			return s.ToArray();
		}

		/// <summary>Builds a replay log the library's Config.LogFile parser understands.</summary>
		static void WriteLogFile(string path, byte[] screenStream)
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
			data.Append("FF EF");
			records.Add(data.ToString());

			using (StreamWriter w = new StreamWriter(path, false))
			{
				int t = 1;
				foreach (string r in records)
				{
					w.WriteLine(t.ToString("D6") + "   " + "H " + "       " + r);
					t++;
				}
			}
		}

		#endregion
	}
}
