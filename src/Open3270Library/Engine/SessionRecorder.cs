#region License
/* 
 *
 * Open3270 - A C# implementation of the TN3270/TN3270E protocol
 *
 * Copyright (c) 2004-2020 Michael Warriner
 * Modifications (c) as per Git change history
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software
 * and associated documentation files (the "Software"), to deal in the Software without restriction,
 * including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
 * and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
 * subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all copies or substantial 
 * portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
 * LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. 
 * IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, 
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
 * SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */
 
#endregion
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Open3270
{
	/// <summary>
	/// Writes a session to a pair of files: a .log of raw wire bytes that
	/// <see cref="ConnectionConfig.LogFile"/> can replay unchanged, and a .json sidecar carrying
	/// everything the log format cannot.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The log line format is fixed by the replay parser, which reads by column rather than by
	/// token. Columns 0 to 5 are HHMMSS and must parse as an integer, column 9 is the direction
	/// ('H' host to client, 'C' client to host), column 10 must be a space, and the space separated
	/// hex bytes start at column 18. Columns 6 to 8 and 11 to 17 are never read. Emitting a prefix
	/// one character short or long makes every line misparse, so the prefix is built to an exact
	/// width here and the round trip test exists to keep it that way.
	/// </para>
	/// <para>
	/// Nothing touches the disk on a calling thread. Lines are formatted by the caller - which also
	/// copies the data out of the reusable buffer - then queued for a writer thread, so a slow or
	/// blocked disk cannot stall the socket receive thread and change the timing that the wait
	/// loops depend on.
	/// </para>
	/// <para>
	/// A recording holds live screen content and everything keyed into the session. Write it
	/// somewhere access controlled.
	/// </para>
	/// </remarks>
	public class SessionRecorder : ISessionRecorder
	{
		#region Fields

		/// <summary>Width of the fixed prefix that precedes the hex bytes on every line.</summary>
		private const int PrefixWidth = 18;

		private readonly string logPath;
		private readonly string sidecarPath;
		private readonly SessionRecordingMetadata metadata;

		/// <summary>
		/// Guards the queue, the keystroke list and the disposal flag. Private by design: the
		/// recorder must never contend on a lock that Telnet holds.
		/// </summary>
		private readonly object sync = new object();

		private readonly Queue<string> pending = new Queue<string>();
		private Thread writerThread;
		private bool stopping;
		private bool disposed;

		/// <summary>
		/// Set when the sidecar no longer reflects the metadata, cleared once it is rewritten.
		/// The sidecar is rewritten as the recording goes rather than only on disposal, because
		/// a recording whose process is killed must still be usable - see the class remarks.
		/// </summary>
		private bool sidecarDirty = true;

		#endregion

		#region Constructors

		/// <summary>
		/// Starts recording to the given path. The sidecar is written beside it with the same base
		/// name and a .json extension, when the recorder is disposed.
		/// </summary>
		/// <param name="path">Path of the .log file to write.</param>
		/// <param name="metadata">
		/// Recording metadata. May be null, in which case an empty sidecar is written.
		/// </param>
		public SessionRecorder(string path, SessionRecordingMetadata metadata)
		{
			if (string.IsNullOrEmpty(path))
			{
				throw new ArgumentNullException("path");
			}

			this.logPath = path;
			this.sidecarPath = Path.ChangeExtension(path, ".json");
			this.metadata = metadata ?? new SessionRecordingMetadata();

			if (string.IsNullOrEmpty(this.metadata.RecordingId))
			{
				this.metadata.RecordingId = Path.GetFileNameWithoutExtension(path);
			}
			this.metadata.CapturedAtUtc = DateTime.UtcNow;

			// Nothing about durability may depend on Dispose being reached. A worker killed mid
			// session, an Environment.Exit, or an unhandled exception on another thread all skip
			// it - the GC will not call it, and .NET does not run finalizers at process exit - so
			// the log is flushed per line and the sidecar is rewritten as it changes.
			this.writerThread = new Thread(this.WriterThreadHandler);
			this.writerThread.Name = "Open3270 session recorder";
			this.writerThread.IsBackground = true;
			this.writerThread.Start();
		}

		#endregion

		#region Properties

		/// <summary>
		/// Path of the .log file being written.
		/// </summary>
		public string LogPath
		{
			get { return this.logPath; }
		}

		/// <summary>
		/// Path of the .json sidecar, written on disposal.
		/// </summary>
		public string SidecarPath
		{
			get { return this.sidecarPath; }
		}

		#endregion

		#region Public Methods

		/// <summary>
		/// Records bytes received from the host.
		/// </summary>
		public void HostToClient(byte[] buffer, int length)
		{
			this.Enqueue('H', buffer, length);
		}

		/// <summary>
		/// Records bytes sent to the host.
		/// </summary>
		public void ClientToHost(byte[] buffer, int length)
		{
			this.Enqueue('C', buffer, length);
		}

		/// <summary>
		/// Records that an action was dispatched, by tag.
		/// </summary>
		public void Keystroke(string tag, int length)
		{
			if (string.IsNullOrEmpty(tag))
			{
				return;
			}

			SessionRecordingKeystroke entry = new SessionRecordingKeystroke();
			entry.Time = Timestamp();
			entry.Tag = tag;
			entry.Length = length;

			lock (this.sync)
			{
				if (this.disposed)
				{
					return;
				}
				this.metadata.Keystrokes.Add(entry);
				this.sidecarDirty = true;
				Monitor.Pulse(this.sync);
			}
		}


		/// <summary>
		/// Marks the sidecar as needing a rewrite, after the caller has changed the metadata it was
		/// given. Use it once the session is connected and the negotiated geometry is known:
		/// <c>metadata.Columns = emulator.ScreenColumns</c> cannot be set before <c>Connect</c>,
		/// and the sidecar should carry it even if the recording is never disposed.
		/// </summary>
		public void MetadataChanged()
		{
			lock (this.sync)
			{
				if (this.disposed)
				{
					return;
				}
				this.sidecarDirty = true;
				Monitor.Pulse(this.sync);
			}
		}

		/// <summary>
		/// Flushes everything queued, stops the writer thread and writes the sidecar.
		/// </summary>
		public void Dispose()
		{
			lock (this.sync)
			{
				if (this.disposed)
				{
					return;
				}
				this.disposed = true;
				this.stopping = true;
				Monitor.Pulse(this.sync);
			}

			Thread thread = this.writerThread;
			if (thread != null)
			{
				thread.Join();
				this.writerThread = null;
			}

			this.WriteSidecar();
		}

		#endregion

		#region Private Methods

		/// <summary>
		/// Formats one line and queues it. Formatting happens here, on the caller's thread, because
		/// it is what copies the bytes out of a buffer the caller is about to reuse.
		/// </summary>
		private void Enqueue(char direction, byte[] buffer, int length)
		{
			if (buffer == null || length <= 0)
			{
				return;
			}

			string line = FormatLine(Timestamp(), direction, buffer, length);

			lock (this.sync)
			{
				if (this.disposed)
				{
					return;
				}
				this.pending.Enqueue(line);
				Monitor.Pulse(this.sync);
			}
		}

		/// <summary>
		/// Builds one log line: an exactly <see cref="PrefixWidth"/> character prefix followed by
		/// space separated lower case hex.
		/// </summary>
		internal static string FormatLine(string time, char direction, byte[] buffer, int length)
		{
			StringBuilder line = new StringBuilder(PrefixWidth + (length * 3));

			line.Append(time);              // columns 0-5,  must parse as an integer
			line.Append("   ");             // columns 6-8,  never read
			line.Append(direction);         // column  9,    'H' or 'C'
			line.Append(' ');               // column  10,   must be a space
			line.Append("       ");         // columns 11-17, never read

			for (int i = 0; i < length; i++)
			{
				if (i > 0)
				{
					line.Append(' ');
				}
				line.Append(buffer[i].ToString("x2", CultureInfo.InvariantCulture));
			}

			return line.ToString();
		}

		/// <summary>
		/// Local time as HHMMSS, which is what the replay parser reads out of columns 0 to 5.
		/// </summary>
		private static string Timestamp()
		{
			return DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
		}

		/// <summary>
		/// Drains the queue to disk until disposal, then drains whatever is left.
		/// </summary>
		private void WriterThreadHandler()
		{
			try
			{
				using (StreamWriter writer = new StreamWriter(this.logPath, false))
				{
					// Flush per line. The cost is a write syscall per record, on this thread and
					// never on the socket receive thread, which is the right trade for a file
					// whose whole purpose is to survive the process that wrote it.
					writer.AutoFlush = true;

					while (true)
					{
						string line = null;
						bool rewriteSidecar = false;

						lock (this.sync)
						{
							while (this.pending.Count == 0 && !this.sidecarDirty && !this.stopping)
							{
								Monitor.Wait(this.sync);
							}

							if (this.pending.Count > 0)
							{
								// Bytes first: drain the log before rewriting the sidecar, so a
								// stopping recorder never leaves queued wire data unwritten.
								line = this.pending.Dequeue();
							}
							else if (this.sidecarDirty)
							{
								this.sidecarDirty = false;
								rewriteSidecar = true;
							}
							else
							{
								break;
							}
						}

						if (line != null)
						{
							writer.WriteLine(line);
						}
						else if (rewriteSidecar)
						{
							this.WriteSidecar();
						}
					}

					writer.Flush();
				}
			}
			catch (Exception e)
			{
				// A recorder must never take the session down with it. The collector is passive by
				// construction, and a failure to record is not a failure of the session.
				Console.WriteLine("Session recorder stopped writing " + this.logPath + ": " + e.Message);
			}
		}

		/// <summary>
		/// Writes the sidecar. Hand rolled rather than serialized so that the schema is legible
		/// next to the format it documents, and so the library keeps its zero dependency build.
		/// </summary>
		private void WriteSidecar()
		{
			try
			{
				StringBuilder json = new StringBuilder();
				json.Append("{\r\n");
				AppendNumber(json, "formatVersion", this.metadata.FormatVersion, true);
				AppendString(json, "recordingId", this.metadata.RecordingId, true);
				AppendString(json, "target", this.metadata.Target, true);
				AppendString(json, "variant", this.metadata.Variant, true);
				AppendString(json, "termType", this.metadata.TermType, true);
				AppendNumber(json, "cx", this.metadata.Columns, true);
				AppendNumber(json, "cy", this.metadata.Rows, true);
				AppendString(json, "engineCommit", this.metadata.EngineCommit, true);
				AppendString(json, "capturedBy", this.metadata.CapturedBy, true);
				AppendString(json, "redaction", this.metadata.Redaction, true);
				AppendString(json, "capturedAt", this.metadata.CapturedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), true);

				json.Append("  \"keystrokes\": [\r\n");
				List<SessionRecordingKeystroke> keystrokes;
				lock (this.sync)
				{
					keystrokes = new List<SessionRecordingKeystroke>(this.metadata.Keystrokes);
				}
				for (int i = 0; i < keystrokes.Count; i++)
				{
					SessionRecordingKeystroke k = keystrokes[i];
					json.Append("    { \"t\": ").Append(Quote(k.Time));
					json.Append(", \"tag\": ").Append(Quote(k.Tag));
					if (k.Length > 0)
					{
						json.Append(", \"len\": ").Append(k.Length.ToString(CultureInfo.InvariantCulture));
					}
					json.Append(" }");
					if (i < keystrokes.Count - 1)
					{
						json.Append(',');
					}
					json.Append("\r\n");
				}
				json.Append("  ]\r\n");
				json.Append("}\r\n");

				File.WriteAllText(this.sidecarPath, json.ToString());
			}
			catch (Exception e)
			{
				Console.WriteLine("Session recorder could not write " + this.sidecarPath + ": " + e.Message);
			}
		}

		private static void AppendString(StringBuilder json, string name, string value, bool comma)
		{
			json.Append("  \"").Append(name).Append("\": ").Append(Quote(value));
			json.Append(comma ? ",\r\n" : "\r\n");
		}

		private static void AppendNumber(StringBuilder json, string name, int value, bool comma)
		{
			json.Append("  \"").Append(name).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
			json.Append(comma ? ",\r\n" : "\r\n");
		}

		/// <summary>
		/// JSON string literal, or null when there is no value.
		/// </summary>
		private static string Quote(string value)
		{
			if (value == null)
			{
				return "null";
			}

			StringBuilder quoted = new StringBuilder(value.Length + 2);
			quoted.Append('"');
			foreach (char c in value)
			{
				switch (c)
				{
					case '"': quoted.Append("\\\""); break;
					case '\\': quoted.Append("\\\\"); break;
					case '\b': quoted.Append("\\b"); break;
					case '\f': quoted.Append("\\f"); break;
					case '\n': quoted.Append("\\n"); break;
					case '\r': quoted.Append("\\r"); break;
					case '\t': quoted.Append("\\t"); break;
					default:
						if (c < ' ')
						{
							quoted.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
						}
						else
						{
							quoted.Append(c);
						}
						break;
				}
			}
			quoted.Append('"');
			return quoted.ToString();
		}

		#endregion
	}
}
