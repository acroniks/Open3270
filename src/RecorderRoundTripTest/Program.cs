using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Open3270;

namespace RecorderRoundTripTest
{
	/// <summary>Records every call so a test can assert which tees fired.</summary>
	class CapturingRecorder : ISessionRecorder
	{
		public int HostToClientCalls;
		public int ClientToHostCalls;
		public readonly List<string> Tags = new List<string>();
		public readonly List<int> Lengths = new List<int>();

		public void HostToClient(byte[] buffer, int length) { HostToClientCalls++; }
		public void ClientToHost(byte[] buffer, int length) { ClientToHostCalls++; }
		public void Keystroke(string tag, int length) { Tags.Add(tag); Lengths.Add(length); }
		public void Dispose() { }
	}

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
			string dir = Path.Combine(Path.GetTempPath(), "open3270-recorder-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);

			try
			{
				Console.WriteLine("---- 1. column discipline ----");
				ColumnDiscipline(dir);

				Console.WriteLine();
				Console.WriteLine("---- 2. round trip through the state machine ----");
				RoundTrip(dir);

				Console.WriteLine();
				Console.WriteLine("---- 3. the replay branch is not recorded ----");
				ReplayIsNotRecorded(dir);

				Console.WriteLine();
				Console.WriteLine("---- 4. a recording survives a process that never disposes ----");
				DurableWithoutDispose(dir);
			}
			finally
			{
				try { Directory.Delete(dir, true); } catch { }
			}

			Console.WriteLine();
			Console.WriteLine(failures == 0 ? "All checks passed." : failures + " check(s) FAILED.");
			return failures == 0 ? 0 : 1;
		}

		#region 1. column discipline

		static void ColumnDiscipline(string dir)
		{
			string path = Path.Combine(dir, "columns.log");
			byte[] host = new byte[] { 0x05, 0xF5, 0xC3, 0x11, 0x40, 0x40, 0x1D, 0xF0, 0xC1, 0x00, 0xFF };
			byte[] client = new byte[] { 0x7D, 0x40, 0x40, 0x11, 0x40, 0x5D };

			using (SessionRecorder recorder = new SessionRecorder(path, null))
			{
				recorder.HostToClient(host, host.Length);
				recorder.ClientToHost(client, client.Length);
			}

			string[] lines = File.ReadAllLines(path);
			Check(lines.Length == 2, "two lines written, got " + lines.Length);
			if (lines.Length != 2)
			{
				return;
			}

			CheckLine(lines[0], 'H', host);
			CheckLine(lines[1], 'C', client);
		}

		static void CheckLine(string line, char direction, byte[] expected)
		{
			string label = "line '" + direction + "'";

			Check(line.Length >= 11, label + ": long enough for the parser's length gate");

			int time;
			bool timeParsed = int.TryParse(line.Substring(0, 6), NumberStyles.None,
				CultureInfo.InvariantCulture, out time);
			Check(timeParsed, label + ": columns 0-5 parse as an integer (Convert.ToInt32 runs before "
				+ "the direction check, so anything else throws)");

			Check(line[9] == direction, label + ": direction character in column 9");
			Check(line[10] == ' ', label + ": column 10 is a space (parser reads Substring(9, 2))");

			// Decode exactly the way the replay parser does.
			string text = line.Substring(18);
			List<byte> decoded = new List<byte>();
			while (text.Length > 1)
			{
				decoded.Add(Convert.ToByte(text.Substring(0, 2), 16));
				text = text.Substring(2).Trim();
			}

			bool same = decoded.Count == expected.Length;
			for (int i = 0; same && i < expected.Length; i++)
			{
				same = decoded[i] == expected[i];
			}
			Check(same, label + ": hex from column 18 decodes back to the " + expected.Length
				+ " bytes that went in, got " + decoded.Count);
		}

		#endregion

		#region 2. round trip

		static void RoundTrip(string dir)
		{
			string path = Path.Combine(dir, "roundtrip.log");

			SessionRecordingMetadata metadata = new SessionRecordingMetadata();
			metadata.Target = "synthetic";
			metadata.Variant = "synthetic/default";
			metadata.TermType = "IBM-3278-2";
			metadata.Columns = 80;
			metadata.Rows = 24;
			metadata.CapturedBy = "RecorderRoundTripTest";

			using (SessionRecorder recorder = new SessionRecorder(path, metadata))
			{
				// Negotiation, delivered the way the socket would deliver it.
				foreach (byte[] record in Negotiation())
				{
					recorder.HostToClient(record, record.Length);
				}

				byte[] screen = BuildScreen();
				recorder.HostToClient(screen, screen.Length);

				recorder.Keystroke("enter", 0);
				recorder.Keystroke("string", 10);
			}

			Check(File.Exists(path), "log written");
			Check(File.Exists(Path.ChangeExtension(path, ".json")), "sidecar written beside it");

			// The sidecar carries the tag and the length, never the text.
			string sidecar = File.ReadAllText(Path.ChangeExtension(path, ".json"));
			Check(sidecar.Contains("\"tag\": \"enter\""), "sidecar records the enter tag");
			Check(sidecar.Contains("\"len\": 10"), "sidecar records a length for the string action");
			Check(sidecar.Contains("\"cx\": 80") && sidecar.Contains("\"cy\": 24"),
				"sidecar carries the negotiated geometry rather than assuming it");

			// Now replay what we just wrote, through the engine that has to read it.
			string dump;
			using (TNEmulator emulator = new TNEmulator())
			{
				emulator.Config.LogFile = new StreamReader(path);
				emulator.Config.ThrowExceptionOnLockedScreen = false;
				emulator.Connect();

				IXMLScreen screen = emulator.CurrentScreenXML;
				Check(screen != null, "a screen arrived from the recording");
				if (screen == null)
				{
					return;
				}

				// IXMLScreen floors CX at 80 and CY at 25 (TnXMLScreen.cs:180-190, a 2008 change),
				// so it reports 80x25 for a 24 row model 2. A sidecar populated from here would
				// claim 25 rows for every model 2 recording, and nothing downstream would notice.
				// Asserted rather than worked around, so that removing the floor shows up here.
				Check(screen.CX == 80 && screen.CY == 25,
					"IXMLScreen reports the floored 80x25, got " + screen.CX + "x" + screen.CY
					+ " - this is why the sidecar cannot be populated from IXMLScreen");

				Check(emulator.ScreenColumns == 80 && emulator.ScreenRows == 24,
					"TNEmulator reports the negotiated 80x24, got " + emulator.ScreenColumns + "x"
					+ emulator.ScreenRows + " - this is what the sidecar's cx/cy must come from");

				dump = screen.Dump();
				emulator.Close();
			}

			Check(dump.Contains("ACCOUNT INQUIRY"),
				"the protected title survived the round trip");
			Check(dump.Contains("ACCOUNT NUMBER:"),
				"the protected label survived the round trip");
			Check(dump.Contains("1234567890"),
				"the unprotected field content survived the round trip");
		}

		#endregion

		#region 3. replay is not recorded

		static void ReplayIsNotRecorded(string dir)
		{
			string path = Path.Combine(dir, "replay.log");

			using (SessionRecorder recorder = new SessionRecorder(path, null))
			{
				foreach (byte[] record in Negotiation())
				{
					recorder.HostToClient(record, record.Length);
				}
				byte[] screen = BuildScreen();
				recorder.HostToClient(screen, screen.Length);
			}

			CapturingRecorder capturing = new CapturingRecorder();

			using (TNEmulator emulator = new TNEmulator())
			{
				emulator.Config.LogFile = new StreamReader(path);
				emulator.Config.ThrowExceptionOnLockedScreen = false;
				emulator.Config.RecordTo = capturing;
				emulator.Connect();

				// Dispatches through Actions.Execute, which is where tags are emitted.
				emulator.SendKey(false, TnKey.Enter, 500);
				emulator.Close();
			}

			Check(capturing.ClientToHostCalls == 0,
				"client bytes were not recorded during replay, got " + capturing.ClientToHostCalls
				+ " call(s) - recording the replay branch would write the recording back out");

			Check(capturing.Tags.Contains("enter"),
				"the enter action was tagged above the wire, tags seen: ["
				+ string.Join(", ", capturing.Tags) + "]");
		}

		#endregion

		#region 4. durable without dispose

		/// <summary>
		/// Nothing about durability may depend on Dispose being reached: a killed worker never
		/// reaches it, the GC will not call it, and .NET does not run finalizers at process exit.
		/// An abandoned recorder must still leave a complete log and a sidecar behind.
		/// </summary>
		static void DurableWithoutDispose(string dir)
		{
			string path = Path.Combine(dir, "abandoned.log");
			string sidecar = Path.ChangeExtension(path, ".json");

			Abandon(path);

			// The writer thread is a background thread, so give it a moment to drain rather than
			// assuming it has - the point of the check is the file, not the timing.
			bool wrote = WaitFor(delegate
			{
				return File.Exists(path) && new FileInfo(path).Length > 0;
			}, 5000);

			Check(wrote, "the log has content without anyone calling Dispose");

			bool complete = WaitFor(delegate
			{
				return File.Exists(path) && File.ReadAllLines(path).Length == 20;
			}, 5000);

			Check(complete, "all 20 records reached the log, got "
				+ (File.Exists(path) ? File.ReadAllLines(path).Length : -1));

			Check(WaitFor(delegate { return File.Exists(sidecar); }, 5000),
				"the sidecar exists without anyone calling Dispose");

			if (File.Exists(sidecar))
			{
				Check(File.ReadAllText(sidecar).Contains("\"tag\": \"enter\""),
					"the sidecar carries the tags recorded before the recorder was abandoned");
			}
		}

		/// <summary>Drops a recorder on the floor, exactly as a killed worker would.</summary>
		static void Abandon(string path)
		{
			byte[] data = new byte[] { 0x05, 0xF5, 0xC3, 0x11, 0x40, 0x40 };
			SessionRecorder recorder = new SessionRecorder(path, null);
			for (int i = 0; i < 20; i++)
			{
				recorder.HostToClient(data, data.Length);
			}
			recorder.Keystroke("enter", 0);
			// No Dispose and no using, deliberately.
		}

		static bool WaitFor(Func<bool> condition, int timeoutMs)
		{
			DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while (DateTime.UtcNow < deadline)
			{
				try
				{
					if (condition())
					{
						return true;
					}
				}
				catch (IOException)
				{
					// The writer thread may hold the file mid write; try again.
				}
				System.Threading.Thread.Sleep(25);
			}
			return false;
		}

		#endregion

		#region synthetic 3270 stream

		static List<byte[]> Negotiation()
		{
			List<byte[]> records = new List<byte[]>();
			records.Add(new byte[] { 0xFF, 0xFD, 0x18 });                    // DO TERMINAL-TYPE
			records.Add(new byte[] { 0xFF, 0xFA, 0x18, 0x01, 0xFF, 0xF0 });  // SB TERMINAL-TYPE SEND SE
			records.Add(new byte[] { 0xFF, 0xFD, 0x19 });                    // DO END-OF-RECORD
			records.Add(new byte[] { 0xFF, 0xFB, 0x19 });                    // WILL END-OF-RECORD
			records.Add(new byte[] { 0xFF, 0xFD, 0x00 });                    // DO BINARY
			records.Add(new byte[] { 0xFF, 0xFB, 0x00 });                    // WILL BINARY
			return records;
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
			if (c >= '0' && c <= '9') return (byte)(0xF0 + (c - '0'));
			switch (c)
			{
				case ' ': return 0x40;
				case ':': return 0x7A;
				case '-': return 0x60;
				default: return 0x40;
			}
		}

		/// <summary>A small formatted screen: a title, a label, and one unprotected field.</summary>
		static byte[] BuildScreen()
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

			s.Add(0xFF);  // IAC EOR terminates the 3270 record
			s.Add(0xEF);
			return s.ToArray();
		}

		#endregion
	}
}
