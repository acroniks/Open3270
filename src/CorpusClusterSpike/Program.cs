using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Open3270;
using Open3270.TN3270;   // XMLScreenField, which IXMLScreen.Fields returns

namespace CorpusClusterSpike
{
	/// <summary>One screen as the host drew it, reduced to what clustering needs.</summary>
	class CapturedScreen
	{
		public string Recording;
		public string Target;
		public int Index;
		public int CX;
		public int CY;
		public bool GeometryFromSidecar;
		public bool Formatted;
		public string Signature;
		public int FieldCount;

		/// <summary>Protected, non-empty text with its position - the anchor candidate pool.</summary>
		public List<Anchor> ProtectedText = new List<Anchor>();

		/// <summary>Kept so a human can eyeball a cluster without re-running the replay.</summary>
		public string Dump;
	}

	class Anchor
	{
		public string Text;
		public int Top;
		public int Left;

		public string Key { get { return Top + "," + Left + "," + Text; } }
		public override string ToString()
		{
			return "\"" + Text + "\" at (" + Top + "," + Left + ")";
		}
	}

	class Cluster
	{
		public string Signature;
		public List<CapturedScreen> Members = new List<CapturedScreen>();
		public List<Anchor> Candidates = new List<Anchor>();
	}

	class Program
	{
		/// <summary>Silence that means the recording has finished replaying.</summary>
		const int QuietMs = 1200;
		const int QuietPollMs = 100;

		/// <summary>Upper bound per recording, however busy it is.</summary>
		const int HardCapMs = 120000;

		static int Main(string[] args)
		{
			if (args.Length < 1)
			{
				Console.WriteLine("usage: CorpusClusterSpike <corpus-dir> [--out <report-dir>]");
				return 2;
			}

			string corpusDir = args[0];
			string outDir = null;
			for (int i = 1; i < args.Length - 1; i++)
			{
				if (args[i] == "--out")
				{
					outDir = args[i + 1];
				}
			}

			if (!Directory.Exists(corpusDir))
			{
				Console.WriteLine("No such directory: " + Path.GetFullPath(corpusDir));
				return 2;
			}

			string[] logs = Directory.GetFiles(corpusDir, "*.log", SearchOption.AllDirectories);
			Array.Sort(logs);

			if (logs.Length == 0)
			{
				Console.WriteLine("No .log recordings under " + Path.GetFullPath(corpusDir));
				return 2;
			}

			Console.WriteLine("Corpus: " + Path.GetFullPath(corpusDir));
			Console.WriteLine(logs.Length + " recording(s)");
			Console.WriteLine();

			string work = Path.Combine(Path.GetTempPath(), "corpus-spike-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(work);

			List<CapturedScreen> screens = new List<CapturedScreen>();
			int failed = 0;

			try
			{
				foreach (string log in logs)
				{
					try
					{
						int before = screens.Count;
						Replay(log, work, screens);
						Console.WriteLine("  " + Path.GetFileName(log) + ": "
							+ (screens.Count - before) + " screen(s)");
					}
					catch (Exception e)
					{
						failed++;
						Console.WriteLine("  " + Path.GetFileName(log) + ": FAILED - " + e.Message);
					}
				}
			}
			finally
			{
				try { Directory.Delete(work, true); } catch { }
			}

			Console.WriteLine();
			Report(screens, logs.Length, failed, outDir);
			return 0;
		}

		#region replay

		/// <summary>
		/// Replays one recording and collects every screen the host drew.
		/// </summary>
		static void Replay(string logPath, string workDir, List<CapturedScreen> into)
		{
			string recordingId = Path.GetFileNameWithoutExtension(logPath);
			string target = ReadSidecarValue(Path.ChangeExtension(logPath, ".json"), "target")
				?? "(unknown)";

			// Strip the client lines. A recording carrying them stalls on replay unless something
			// is driving it with matching keystrokes, and the host lines alone redraw every screen.
			string hOnly = Path.Combine(workDir, recordingId + ".h.log");
			int stripped = StripClientLines(logPath, hOnly);

			List<CapturedScreen> captured = new List<CapturedScreen>();
			object gate = new object();
			int cx = ReadSidecarInt(Path.ChangeExtension(logPath, ".json"), "cx");
			int cy = ReadSidecarInt(Path.ChangeExtension(logPath, ".json"), "cy");

			// Completion is detected by quiescence rather than by waiting for a disconnect event:
			// the replay thread simply stops when the recording runs out, and a screen that never
			// arrives is indistinguishable from one that has not arrived yet except by waiting.
			long lastArrival = DateTime.UtcNow.Ticks;

			using (TNEmulator emulator = new TNEmulator())
			{
				emulator.Config.LogFile = new StreamReader(hOnly);
				emulator.Config.ThrowExceptionOnLockedScreen = false;

				emulator.ScreenArrived += delegate(IXMLScreen screen)
				{
					CapturedScreen snapshot;
					lock (gate)
					{
						snapshot = Reduce(screen, recordingId, target, captured.Count, cx, cy);
						captured.Add(snapshot);
						lastArrival = DateTime.UtcNow.Ticks;
					}
				};

				emulator.Connect();

				DateTime deadline = DateTime.UtcNow.AddMilliseconds(HardCapMs);
				while (DateTime.UtcNow < deadline)
				{
					Thread.Sleep(QuietPollMs);

					long idleMs;
					lock (gate)
					{
						idleMs = (DateTime.UtcNow.Ticks - lastArrival) / TimeSpan.TicksPerMillisecond;
					}

					if (idleMs >= QuietMs)
					{
						break;
					}
				}

				emulator.Close();
			}

			lock (gate)
			{
				into.AddRange(captured);
			}
		}

		/// <summary>
		/// Copies a recording keeping only host lines. Column 9 carries the direction, and the
		/// parser requires columns 0-5 to parse as an integer, so anything malformed is dropped
		/// rather than risking a misparse.
		/// </summary>
		static int StripClientLines(string source, string destination)
		{
			int stripped = 0;
			using (StreamReader reader = new StreamReader(source))
			using (StreamWriter writer = new StreamWriter(destination, false))
			{
				string line;
				while ((line = reader.ReadLine()) != null)
				{
					if (line.Length < 11)
					{
						continue;
					}

					int time;
					if (!int.TryParse(line.Substring(0, 6), NumberStyles.None,
						CultureInfo.InvariantCulture, out time))
					{
						continue;
					}

					if (line[9] == 'C')
					{
						stripped++;
						continue;
					}

					if (line[9] == 'H')
					{
						writer.WriteLine(line);
					}
				}
			}
			return stripped;
		}

		#endregion

		#region reduction

		/// <summary>
		/// Reduces a screen to its geometry signature plus its protected text.
		/// </summary>
		static CapturedScreen Reduce(IXMLScreen screen, string recording, string target, int index,
			int sidecarCx, int sidecarCy)
		{
			CapturedScreen result = new CapturedScreen();
			result.Recording = recording;
			result.Target = target;
			result.Index = index;

			// Prefer the sidecar's negotiated geometry. IXMLScreen.CX/CY is floored at 80x25, so
			// it reports 25 rows for a 24 row model 2 and would misreport every model 2 recording.
			result.CX = sidecarCx > 0 ? sidecarCx : screen.CX;
			result.CY = sidecarCy > 0 ? sidecarCy : screen.CY;
			result.GeometryFromSidecar = sidecarCx > 0 && sidecarCy > 0;

			XMLScreenField[] fields = screen.Fields;
			result.Formatted = fields != null && fields.Length > 0;
			result.FieldCount = fields == null ? 0 : fields.Length;

			try
			{
				result.Dump = screen.Dump();
			}
			catch (Exception)
			{
				result.Dump = "(dump unavailable)";
			}

			if (!result.Formatted)
			{
				result.Signature = string.Empty;
				return result;
			}

			// Location and Protected only. Never text length: a blank field comes back as
			// string.Empty, so text length is a property of the data, not of the screen.
			var ordered = fields
				.Where(f => f != null && f.Location != null)
				.OrderBy(f => f.Location.top)
				.ThenBy(f => f.Location.left)
				.ToList();

			StringBuilder signature = new StringBuilder();
			foreach (XMLScreenField field in ordered)
			{
				bool isProtected = field.Attributes != null && field.Attributes.Protected;

				signature.Append(field.Location.top).Append(',')
					.Append(field.Location.left).Append(',')
					.Append(field.Location.length).Append(',')
					.Append(isProtected ? 'P' : 'u').Append(';');

				if (isProtected && !string.IsNullOrEmpty(field.Text))
				{
					string text = field.Text.Trim();
					if (text.Length > 0 && !IsNavField(field.Location.top, field.Location.left))
					{
						Anchor anchor = new Anchor();
						anchor.Text = text;
						anchor.Top = field.Location.top;
						anchor.Left = field.Location.left;
						result.ProtectedText.Add(anchor);
					}
				}
			}

			result.Signature = signature.ToString();
			return result;
		}

		/// <summary>
		/// The nav field sits at the top left and cannot identify anything - it is an echo of what
		/// was typed, and its protected parts appear on every screen. No node may anchor there.
		/// </summary>
		static bool IsNavField(int top, int left)
		{
			return top == 0 && left < 24;
		}

		#endregion

		#region report

		static void Report(List<CapturedScreen> screens, int recordings, int failed, string outDir)
		{
			List<CapturedScreen> unformatted = screens.Where(s => !s.Formatted).ToList();
			List<CapturedScreen> formatted = screens.Where(s => s.Formatted).ToList();

			Console.WriteLine("================ corpus ================");
			Console.WriteLine("  recordings replayed  " + (recordings - failed) + " of " + recordings);
			Console.WriteLine("  screens captured     " + screens.Count);
			Console.WriteLine("  distinct targets     " + screens.Select(s => s.Target).Distinct().Count()
				+ "  [" + string.Join(", ", screens.Select(s => s.Target).Distinct().OrderBy(t => t)) + "]");
			Console.WriteLine("  distinct recordings  " + screens.Select(s => s.Recording).Distinct().Count());

			var geometries = screens.Select(s => s.CX + "x" + s.CY).Distinct().OrderBy(g => g).ToList();
			bool fromSidecar = screens.Count > 0 && screens.All(s => s.GeometryFromSidecar);
			Console.WriteLine("  screen geometries    " + string.Join(", ", geometries)
				+ (fromSidecar
					? "  (negotiated, from the sidecar)"
					: "  (from IXMLScreen, which floors at 80x25 - sidecars missing or incomplete)"));

			if (unformatted.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("  unformatted screens  " + unformatted.Count
					+ " - segregated before clustering, since an empty signature would collide them all");
			}

			// Cluster on exact signature match.
			Dictionary<string, Cluster> clusters = new Dictionary<string, Cluster>();
			foreach (CapturedScreen screen in formatted)
			{
				Cluster cluster;
				if (!clusters.TryGetValue(screen.Signature, out cluster))
				{
					cluster = new Cluster();
					cluster.Signature = screen.Signature;
					clusters[screen.Signature] = cluster;
				}
				cluster.Members.Add(screen);
			}

			List<Cluster> ordered = clusters.Values
				.OrderByDescending(c => c.Members.Count)
				.ToList();

			ComputeAnchorCandidates(ordered);

			int S = formatted.Count;
			int C = ordered.Count;
			int recordingCount = formatted.Select(s => s.Recording).Distinct().Count();

			// The real H1 question is not the S to C ratio, which just tracks how many screens a
			// flow visits. It is whether one signature holds across *different data*. A cluster
			// drawn from two or more recordings is exactly that evidence; a corpus of singletons
			// is exactly its absence.
			int spanning = ordered.Count(c => c.Members.Select(m => m.Recording).Distinct().Count() > 1);
			int singletons = ordered.Count(c => c.Members.Count == 1);

			Console.WriteLine();
			Console.WriteLine("================ the verdict ================");
			Console.WriteLine("  S, formatted screens captured   " + S);
			Console.WriteLine("  C, distinct signatures          " + C);
			Console.WriteLine("  clusters spanning >1 recording  " + spanning + " of " + C);
			Console.WriteLine("  single-member clusters          " + singletons + " of " + C);
			Console.WriteLine();

			if (S == 0)
			{
				Console.WriteLine("  Nothing to judge - no formatted screens were captured.");
			}
			else if (recordingCount < 2)
			{
				Console.WriteLine("  CANNOT JUDGE H1. Only one recording contributed screens, so nothing here");
				Console.WriteLine("  varies the data. H1 is the claim that a signature survives a change of");
				Console.WriteLine("  account - it needs the same screens walked for several different accounts.");
				Console.WriteLine("  Collect more before reading anything into these counts.");
			}
			else if (spanning == 0)
			{
				Console.WriteLine("  H1 LOOKS FALSIFIED. No signature recurred across recordings, so geometry");
				Console.WriteLine("  is splitting on data rather than on screen identity. The likeliest cause");
				Console.WriteLine("  is a detail area whose row count varies per account, which adds and");
				Console.WriteLine("  removes fields. Compare two dumps of the same screen from --out.");
			}
			else if (spanning * 2 >= C)
			{
				Console.WriteLine("  H1 LOOKS SUPPORTED. Most signatures recur across different recordings,");
				Console.WriteLine("  which is what stability across accounts looks like.");
				Console.WriteLine();
				Console.WriteLine("  H2 is yours to judge: is C close to the number of screens you would name");
				Console.WriteLine("  walking the system yourself? Materially below it means geometry is");
				Console.WriteLine("  colliding screens a human calls different. Read the dumps to decide.");
			}
			else
			{
				Console.WriteLine("  MIXED. Some signatures recur across recordings and many do not. Either");
				Console.WriteLine("  the corpus is thin - screens visited by only one recording cannot show");
				Console.WriteLine("  recurrence - or a subset of screens is data-dependent. The single-member");
				Console.WriteLine("  clusters below are the ones to look at first.");
			}

			// Screens seen under more than one signature are the direct H1 counter-example: same
			// recording position, different geometry.
			var multi = formatted
				.GroupBy(s => s.Recording + "#" + s.Index)
				.Where(g => g.Select(x => x.Signature).Distinct().Count() > 1)
				.ToList();
			if (multi.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("  " + multi.Count + " screen position(s) produced more than one signature.");
			}

			Console.WriteLine();
			Console.WriteLine("================ clusters ================");
			int shown = 0;
			foreach (Cluster cluster in ordered)
			{
				shown++;
				var recs = cluster.Members.Select(m => m.Recording).Distinct().ToList();
				var targets = cluster.Members.Select(m => m.Target).Distinct().ToList();

				Console.WriteLine();
				Console.WriteLine("  [" + shown.ToString("D2") + "]  " + cluster.Members.Count
					+ " screen(s), " + cluster.Members[0].FieldCount + " fields, "
					+ recs.Count + " recording(s), targets: " + string.Join(", ", targets));

				if (recs.Count == 1 && cluster.Members.Count == 1)
				{
					Console.WriteLine("        seen once only - not yet evidence of anything");
				}

				if (cluster.Candidates.Count == 0)
				{
					Console.WriteLine("        NO ANCHOR CANDIDATE - no protected text unique to this cluster");
				}
				else
				{
					int rank = 0;
					foreach (Anchor candidate in cluster.Candidates.Take(5))
					{
						rank++;
						Console.WriteLine("        " + rank + ". " + candidate);
					}
				}
			}

			ReportNearMisses(ordered);

			int anchorless = ordered.Count(c => c.Candidates.Count == 0);
			Console.WriteLine();
			Console.WriteLine("================ anchors ================");
			Console.WriteLine("  clusters with a candidate     " + (C - anchorless) + " of " + C);
			if (anchorless > 0)
			{
				Console.WriteLine("  clusters with none            " + anchorless
					+ " - these are phase 02's expensive screens");
			}

			if (outDir != null)
			{
				WriteDumps(ordered, unformatted, outDir);
			}
			else
			{
				Console.WriteLine();
				Console.WriteLine("  Pass --out <dir> to write one dump per cluster for eyeballing.");
			}
		}

		/// <summary>
		/// Reports clusters whose signatures are prefix-compatible but different lengths. That is
		/// the fingerprint of one logical screen with a variable-height region - a detail area with
		/// a row per transaction - which is the most likely way H1 fails. Naming the pattern beats
		/// leaving it to be inferred from field counts.
		/// </summary>
		static void ReportNearMisses(List<Cluster> clusters)
		{
			List<List<Cluster>> families = new List<List<Cluster>>();
			HashSet<Cluster> claimed = new HashSet<Cluster>();

			// A signature already seen across several recordings has survived a change of account,
			// so it is stable by observation and cannot be the data-dependent one. Excluding those
			// keeps unrelated screens out of a family: two different screens often share their
			// opening fields - a title on row 0 is enough - and would otherwise look prefixed.
			List<Cluster> candidates = clusters
				.Where(c => c.Members.Select(m => m.Recording).Distinct().Count() == 1)
				.ToList();

			// Shortest first, so the stem of a family is found before its longer relatives.
			List<Cluster> bySize = candidates.OrderBy(c => c.Signature.Length).ToList();

			foreach (Cluster stem in bySize)
			{
				if (claimed.Contains(stem) || stem.Signature.Length == 0)
				{
					continue;
				}

				List<Cluster> family = new List<Cluster> { stem };
				foreach (Cluster other in bySize)
				{
					if (!ReferenceEquals(other, stem)
						&& !claimed.Contains(other)
						&& other.Signature.Length > stem.Signature.Length
						&& other.Signature.StartsWith(stem.Signature, StringComparison.Ordinal))
					{
						family.Add(other);
					}
				}

				if (family.Count > 1)
				{
					foreach (Cluster member in family)
					{
						claimed.Add(member);
					}
					families.Add(family);
				}
			}

			if (families.Count == 0)
			{
				return;
			}

			Console.WriteLine();
			Console.WriteLine("================ near misses ================");
			Console.WriteLine("  Signatures below are prefix-compatible but different lengths, which is");
			Console.WriteLine("  what one screen with a variable-height region looks like: the fields of");
			Console.WriteLine("  the shorter signature are the start of the longer one, and the extra");
			Console.WriteLine("  fields are repeated rows. If these are one screen to you, H1 is falsified");
			Console.WriteLine("  for it and geometry cannot be its primary discriminator.");
			Console.WriteLine();
			Console.WriteLine("  Only signatures seen in a single recording are considered here - one that");
			Console.WriteLine("  recurred across accounts has already shown it does not follow the data.");

			foreach (List<Cluster> family in families)
			{
				Console.WriteLine();
				Console.WriteLine("  family of " + family.Count + ":");
				foreach (Cluster member in family)
				{
					int index = clusters.IndexOf(member) + 1;
					Console.WriteLine("    [" + index.ToString("D2") + "]  "
						+ member.Members[0].FieldCount + " fields, "
						+ member.Members.Count + " screen(s), from "
						+ string.Join(", ", member.Members.Select(m => m.Recording).Distinct()));
				}
			}
		}


		/// <summary>
		/// An anchor candidate is protected text present in every member of one cluster and in no
		/// member of any other. That is text identifying the screen which survives a change of data.
		/// </summary>
		static void ComputeAnchorCandidates(List<Cluster> clusters)
		{
			Dictionary<Cluster, HashSet<string>> common = new Dictionary<Cluster, HashSet<string>>();

			foreach (Cluster cluster in clusters)
			{
				HashSet<string> shared = null;
				foreach (CapturedScreen member in cluster.Members)
				{
					HashSet<string> here = new HashSet<string>(member.ProtectedText.Select(a => a.Key));
					if (shared == null)
					{
						shared = here;
					}
					else
					{
						shared.IntersectWith(here);
					}
				}
				common[cluster] = shared ?? new HashSet<string>();
			}

			foreach (Cluster cluster in clusters)
			{
				HashSet<string> elsewhere = new HashSet<string>();
				foreach (Cluster other in clusters)
				{
					if (!ReferenceEquals(other, cluster))
					{
						foreach (CapturedScreen member in other.Members)
						{
							foreach (Anchor anchor in member.ProtectedText)
							{
								elsewhere.Add(anchor.Key);
							}
						}
					}
				}

				Dictionary<string, Anchor> byKey = new Dictionary<string, Anchor>();
				foreach (CapturedScreen member in cluster.Members)
				{
					foreach (Anchor anchor in member.ProtectedText)
					{
						byKey[anchor.Key] = anchor;
					}
				}

				cluster.Candidates = common[cluster]
					.Where(k => !elsewhere.Contains(k))
					.Select(k => byKey[k])
					.OrderByDescending(Score)
					.ToList();
			}
		}

		/// <summary>
		/// Ranks a candidate by how far it sits from anything data-shaped. Letters and length are
		/// good; digits, dates and money are how host output looks.
		/// </summary>
		static int Score(Anchor anchor)
		{
			string text = anchor.Text;
			int score = Math.Min(text.Length, 30);

			int digits = text.Count(char.IsDigit);
			int letters = text.Count(char.IsLetter);

			score += letters * 2;
			score -= digits * 6;

			if (text.Any(c => c == '/' || c == '.' || c == ',' || c == '$'))
			{
				score -= 10;
			}
			if (digits == 0)
			{
				score += 12;
			}
			if (anchor.Top <= 2)
			{
				score += 8;   // titles and screen ids live at the top
			}
			if (text.Length < 3)
			{
				score -= 15;  // too short to be a dependable anchor
			}

			return score;
		}

		static void WriteDumps(List<Cluster> clusters, List<CapturedScreen> unformatted, string outDir)
		{
			Directory.CreateDirectory(outDir);
			int n = 0;
			foreach (Cluster cluster in clusters)
			{
				n++;
				StringBuilder text = new StringBuilder();
				text.AppendLine("cluster " + n.ToString("D2")
					+ "   members: " + cluster.Members.Count
					+ "   fields: " + cluster.Members[0].FieldCount);
				text.AppendLine("targets: " + string.Join(", ",
					cluster.Members.Select(m => m.Target).Distinct()));
				text.AppendLine();
				text.AppendLine("anchor candidates:");
				foreach (Anchor candidate in cluster.Candidates.Take(10))
				{
					text.AppendLine("  " + candidate);
				}
				text.AppendLine();
				text.AppendLine("first member (" + cluster.Members[0].Recording
					+ " screen " + cluster.Members[0].Index + "):");
				text.AppendLine(cluster.Members[0].Dump);

				File.WriteAllText(Path.Combine(outDir, "cluster-" + n.ToString("D2") + ".txt"),
					text.ToString());
			}

			if (unformatted.Count > 0)
			{
				File.WriteAllText(Path.Combine(outDir, "unformatted.txt"),
					string.Join(Environment.NewLine + Environment.NewLine,
						unformatted.Select(u => u.Recording + " screen " + u.Index
							+ Environment.NewLine + u.Dump)));
			}

			Console.WriteLine();
			Console.WriteLine("  Dumps written to " + Path.GetFullPath(outDir));
		}

		#endregion

		#region sidecar

		/// <summary>
		/// Pulls one string value out of the sidecar. Deliberately not a JSON parser: the sidecar
		/// schema is fixed and flat, and the spike should not need a dependency to read it.
		/// </summary>
		/// <summary>Reads a numeric sidecar value, or 0 when it is absent.</summary>
		static int ReadSidecarInt(string sidecarPath, string name)
		{
			string raw = ReadSidecarValue(sidecarPath, name);
			int value;
			if (raw != null && int.TryParse(raw, NumberStyles.Integer,
				CultureInfo.InvariantCulture, out value))
			{
				return value;
			}
			return 0;
		}


		static string ReadSidecarValue(string sidecarPath, string name)
		{
			if (!File.Exists(sidecarPath))
			{
				return null;
			}

			try
			{
				foreach (string line in File.ReadAllLines(sidecarPath))
				{
					string trimmed = line.Trim();
					string prefix = "\"" + name + "\":";
					if (trimmed.StartsWith(prefix))
					{
						string value = trimmed.Substring(prefix.Length).Trim().TrimEnd(',').Trim();
						if (value == "null")
						{
							return null;
						}
						return value.Trim('"');
					}
				}
			}
			catch (IOException)
			{
			}
			return null;
		}

		#endregion
	}
}
