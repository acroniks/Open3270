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

		/// <summary>Protected text on the title row, which on some systems names the screen.</summary>
		public string Title;
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

		/// <summary>
		/// Row whose protected text is treated as the screen's title. Zero based.
		/// </summary>
		static int TitleRow = 0;

		/// <summary>
		/// Column range of the title, when the title row carries account data as well as chrome.
		/// Negative means "the whole row", which is only right when the row is chrome throughout.
		/// Pinning to columns is what a binding does - GetText(x, y, len) at fixed coordinates -
		/// so setting these is also a rehearsal of the recognizer.
		/// </summary>
		static int TitleLeft = -1;
		static int TitleLength = -1;

		/// <summary>
		/// Rows excluded from the signature and from anchor candidates.
		/// </summary>
		/// <remarks>
		/// A screen with a freeform entry area - a memo someone types into - has field geometry
		/// there that follows what was typed, so those rows make the signature unstable by design
		/// rather than by accident. The design already excludes the message line from screen
		/// identity for the same reason; a user-authored region is the general case of it.
		/// Declaring the rows here is the same act a binding performs.
		/// </remarks>
		static readonly HashSet<int> IgnoredRows = new HashSet<int>();


		/// <summary>
		/// Upper bound per recording, however busy it is. Generous by default: a bulk scrape of
		/// tens of thousands of accounts is one long session, and silently truncating it produces
		/// a confident answer from a fraction of the corpus.
		/// </summary>
		static int HardCapMs = 3600000;

		/// <summary>
		/// Dumps kept per distinct signature. A dump is the whole rendered screen, so keeping one
		/// per screen costs hundreds of megabytes on a large corpus - and the second and later
		/// examples of a signature add nothing a human will read.
		/// </summary>
		const int DumpsPerSignature = 3;

		static int Main(string[] args)
		{
			if (args.Length < 1)
			{
				Console.WriteLine("usage: CorpusClusterSpike <corpus-dir> [--out <report-dir>]");
				Console.WriteLine("                          [--max-seconds <n>] [--diff <a> <b>]");
				Console.WriteLine("                          [--title-row <n>]   (0 based, default 0)");
				Console.WriteLine("                          [--title-cols <left> <len>]");
				Console.WriteLine("                          [--ignore-rows 6-8,22]  excluded from identity");
				return 2;
			}

			string corpusDir = args[0];
			string outDir = null;
			int diffA = -1, diffB = -1;
			for (int i = 1; i < args.Length - 1; i++)
			{
				if (args[i] == "--out")
				{
					outDir = args[i + 1];
				}
				else if (args[i] == "--max-seconds")
				{
					int seconds;
					if (int.TryParse(args[i + 1], out seconds) && seconds > 0)
					{
						HardCapMs = seconds * 1000;
					}
				}
				else if (args[i] == "--title-cols" && i + 2 < args.Length)
				{
					int.TryParse(args[i + 1], out TitleLeft);
					int.TryParse(args[i + 2], out TitleLength);
				}
				else if (args[i] == "--ignore-rows")
				{
					ParseRows(args[i + 1]);
				}
				else if (args[i] == "--title-row")
				{
					int row;
					if (int.TryParse(args[i + 1], out row) && row >= 0)
					{
						TitleRow = row;
					}
				}
				else if (args[i] == "--diff" && i + 2 < args.Length)
				{
					int.TryParse(args[i + 1], out diffA);
					int.TryParse(args[i + 2], out diffB);
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
			List<Cluster> clusters = Report(screens, logs.Length, failed, outDir);

			if (diffA > 0 && diffB > 0)
			{
				DiffClusters(clusters, diffA, diffB);
			}

			return 0;
		}

		#region replay

		/// <summary>
		/// Replays one recording and collects every screen the host drew.
		/// </summary>
		static bool truncated;
		static readonly Dictionary<string, int> dumpsKept = new Dictionary<string, int>();

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

						// Keep only the first few dumps per signature. On a bulk scrape this is
						// the difference between a few megabytes and a few hundred.
						int seen;
						dumpsKept.TryGetValue(snapshot.Signature, out seen);
						if (seen >= DumpsPerSignature)
						{
							snapshot.Dump = null;
						}
						else
						{
							dumpsKept[snapshot.Signature] = seen + 1;
						}

						captured.Add(snapshot);
						lastArrival = DateTime.UtcNow.Ticks;
					}
				};

				emulator.Connect();

				DateTime deadline = DateTime.UtcNow.AddMilliseconds(HardCapMs);
				bool quiet = false;
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
						quiet = true;
						break;
					}
				}

				if (!quiet)
				{
					// Loudly, because the alternative is a confident verdict drawn from however
					// much of the recording happened to fit inside the cap.
					truncated = true;
					Console.WriteLine("    *** TRUNCATED at " + (HardCapMs / 1000) + "s with "
						+ captured.Count + " screen(s) captured - the recording was still"
						+ " producing screens. Raise --max-seconds; this replay is incomplete.");
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
			StringBuilder title = new StringBuilder();
			foreach (XMLScreenField field in ordered)
			{
				if (IgnoredRows.Contains(field.Location.top))
				{
					continue;
				}

				bool isProtected = field.Attributes != null && field.Attributes.Protected;

				// Location.length is the gap to the next field attribute byte, not the length of
				// the text in the field. It therefore never varies independently of the following
				// field's left, which is already in the signature - so it adds no discrimination
				// that position does not, and removing it would collapse nothing. Measured.
				signature.Append(field.Location.top).Append(',')
					.Append(field.Location.left).Append(',')
					.Append(field.Location.length).Append(',')
					.Append(isProtected ? 'P' : 'u').Append(';');

				if (isProtected && !string.IsNullOrEmpty(field.Text))
				{
					string text = field.Text.Trim();
					if (text.Length > 0)
					{
						// The title is whatever protected text sits on the title row. Deliberately
						// not filtered by the nav-field rule below: that rule is about what may
						// serve as an anchor, and a screen that names itself on the same row as
						// the nav field still names itself. Any constant nav-field text picked up
						// here is constant everywhere, so it does not affect the grouping.
						if (field.Location.top == TitleRow)
						{
							title.Append(title.Length > 0 ? " " : "").Append(text);
						}

						if (!IsNavField(field.Location.top, field.Location.left))
						{
							Anchor anchor = new Anchor();
							anchor.Text = text;
							anchor.Top = field.Location.top;
							anchor.Left = field.Location.left;
							result.ProtectedText.Add(anchor);
						}
					}
				}
			}

			result.Signature = signature.ToString();

			if (TitleLeft >= 0 && TitleLength > 0)
			{
				// Read it the way a binding would, by coordinates, rather than by walking fields.
				try
				{
					string pinned = screen.GetText(TitleLeft, TitleRow, TitleLength);
					result.Title = pinned == null ? string.Empty : pinned.Trim();
				}
				catch (Exception)
				{
					result.Title = string.Empty;
				}
			}
			else
			{
				result.Title = title.ToString();
			}

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

		static List<Cluster> Report(List<CapturedScreen> screens, int recordings, int failed, string outDir)
		{
			List<CapturedScreen> unformatted = screens.Where(s => !s.Formatted).ToList();
			List<CapturedScreen> formatted = screens.Where(s => s.Formatted).ToList();

			Console.WriteLine("================ corpus ================");
			if (IgnoredRows.Count > 0)
			{
				Console.WriteLine("  rows excluded        " + string.Join(", ",
					IgnoredRows.OrderBy(r => r)) + "  (--ignore-rows)");
			}
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
				string title = cluster.Members
					.Select(m => m.Title)
					.FirstOrDefault(t => !string.IsNullOrEmpty(t));

				Console.WriteLine("  [" + shown.ToString("D2") + "]  " + cluster.Members.Count
					+ " screen(s), " + cluster.Members[0].FieldCount + " fields, "
					+ recs.Count + " recording(s), targets: " + string.Join(", ", targets));
				if (!string.IsNullOrEmpty(title))
				{
					Console.WriteLine("        title: \"" + title + "\"");
				}

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

			ReportOutliers(ordered);
			ReportTitles(formatted, ordered);
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

			if (truncated)
			{
				Console.WriteLine();
				Console.WriteLine("  *** At least one replay was TRUNCATED. Everything above is drawn from a");
				Console.WriteLine("      fraction of the corpus. Re-run with a larger --max-seconds before");
				Console.WriteLine("      drawing any conclusion from these counts.");
			}

			if (outDir != null)
			{
				WriteDumps(ordered, unformatted, outDir);
			}
			else
			{
				Console.WriteLine();
				Console.WriteLine("  Pass --out <dir> to write one dump per cluster for eyeballing,");
				Console.WriteLine("  or --diff <a> <b> to see exactly which fields two clusters differ by.");
			}

			return ordered;
		}


		/// <summary>
		/// Reports exactly which field tuples two clusters differ by. Two signatures for what looks
		/// like one screen is H1 failing for that screen, and the useful question is not that they
		/// differ but which fields do - a repeating block is a variable-height region, a single
		/// field appearing or vanishing is a conditional one.
		/// </summary>
		static void DiffClusters(List<Cluster> clusters, int a, int b)
		{
			if (a > clusters.Count || b > clusters.Count)
			{
				Console.WriteLine();
				Console.WriteLine("  --diff: no such cluster (there are " + clusters.Count + ")");
				return;
			}

			Cluster left = clusters[a - 1];
			Cluster right = clusters[b - 1];

			List<string> lf = left.Signature.Split(';').Where(s => s.Length > 0).ToList();
			List<string> rf = right.Signature.Split(';').Where(s => s.Length > 0).ToList();

			Console.WriteLine();
			Console.WriteLine("================ diff [" + a.ToString("D2") + "] vs ["
				+ b.ToString("D2") + "] ================");
			Console.WriteLine("  [" + a.ToString("D2") + "]  " + lf.Count + " fields, "
				+ left.Members.Count + " screen(s)");
			Console.WriteLine("  [" + b.ToString("D2") + "]  " + rf.Count + " fields, "
				+ right.Members.Count + " screen(s)");

			HashSet<string> inLeft = new HashSet<string>(lf);
			HashSet<string> inRight = new HashSet<string>(rf);

			List<string> onlyLeft = lf.Where(f => !inRight.Contains(f)).ToList();
			List<string> onlyRight = rf.Where(f => !inLeft.Contains(f)).ToList();

			Console.WriteLine();
			Console.WriteLine("  fields in common               " + lf.Count(f => inRight.Contains(f)));
			Console.WriteLine("  only in [" + a.ToString("D2") + "]                  " + onlyLeft.Count);
			Console.WriteLine("  only in [" + b.ToString("D2") + "]                  " + onlyRight.Count);

			Print("only in [" + a.ToString("D2") + "]", onlyLeft);
			Print("only in [" + b.ToString("D2") + "]", onlyRight);

			Console.WriteLine();
			if (onlyLeft.Count == 0 && onlyRight.Count > 0)
			{
				Console.WriteLine("  [" + a.ToString("D2") + "] is a strict subset of ["
					+ b.ToString("D2") + "]. Fields are added, never moved - a region that grows");
				Console.WriteLine("  with the data, or a conditional field that is sometimes absent.");
			}
			else if (onlyRight.Count == 0 && onlyLeft.Count > 0)
			{
				Console.WriteLine("  [" + b.ToString("D2") + "] is a strict subset of ["
					+ a.ToString("D2") + "]. See above, the other way round.");
			}
			else if (onlyLeft.Count == onlyRight.Count)
			{
				string shifted = DescribeShift(onlyLeft, onlyRight);
				if (shifted != null)
				{
					Console.WriteLine("  " + shifted);
					Console.WriteLine("  A field changes width and everything after it on the row moves by the");
					Console.WriteLine("  same amount. The host is building that line by concatenation rather than");
					Console.WriteLine("  writing into fixed width slots, so the field is host output, not chrome.");
					Console.WriteLine();
					Console.WriteLine("  Two consequences. Geometry cannot key this screen - identify it by its");
					Console.WriteLine("  title instead. And every field after the variable one has no fixed");
					Console.WriteLine("  coordinates, so its binding has to locate it relative to a label rather");
					Console.WriteLine("  than at an absolute position.");
				}
				else
				{
					Console.WriteLine("  Same field count on both sides, different positions or lengths, with no");
					Console.WriteLine("  consistent shift. More likely two genuinely different screens, or one");
					Console.WriteLine("  screen reached by two paths.");
				}
			}

			Console.WriteLine();
			Console.WriteLine("  Field tuples read (top,left,length,P|u), P protected and u unprotected.");
		}

		/// <summary>
		/// Detects the concatenation fingerprint: on some row, one field changes extent and every
		/// field after it shifts by that same amount. Returns a description, or null if the two
		/// sides do not line up that way.
		/// </summary>
		static string DescribeShift(List<string> left, List<string> right)
		{
			var l = left.Select(Parse).Where(t => t != null).OrderBy(t => t[0]).ThenBy(t => t[1]).ToList();
			var r = right.Select(Parse).Where(t => t != null).OrderBy(t => t[0]).ThenBy(t => t[1]).ToList();

			if (l.Count == 0 || l.Count != r.Count)
			{
				return null;
			}

			// The pair must start at the same place, then diverge by a constant.
			if (l[0][0] != r[0][0] || l[0][1] != r[0][1])
			{
				return null;
			}

			int delta = r[0][2] - l[0][2];
			if (delta == 0)
			{
				return null;
			}

			for (int i = 1; i < l.Count; i++)
			{
				if (l[i][0] != r[i][0] || r[i][1] - l[i][1] != delta)
				{
					return null;
				}
			}

			return "Row " + l[0][0] + ": the field at column " + l[0][1] + " changes width by "
				+ Math.Abs(delta) + ", and the " + (l.Count - 1)
				+ " field(s) after it shift by the same " + Math.Abs(delta) + ".";
		}

		/// <summary>Parses "6-8,22" into the set of rows to exclude.</summary>
		static void ParseRows(string spec)
		{
			foreach (string part in spec.Split(','))
			{
				string piece = part.Trim();
				int dash = piece.IndexOf('-');
				int single, from, to;

				if (dash > 0
					&& int.TryParse(piece.Substring(0, dash), out from)
					&& int.TryParse(piece.Substring(dash + 1), out to))
				{
					for (int row = Math.Min(from, to); row <= Math.Max(from, to); row++)
					{
						IgnoredRows.Add(row);
					}
				}
				else if (int.TryParse(piece, out single))
				{
					IgnoredRows.Add(single);
				}
			}
		}


		/// <summary>Parses a "top,left,length,P|u" tuple into its three numbers.</summary>
		static int[] Parse(string tuple)
		{
			string[] parts = tuple.Split(',');
			int top, left, length;
			if (parts.Length < 4
				|| !int.TryParse(parts[0], out top)
				|| !int.TryParse(parts[1], out left)
				|| !int.TryParse(parts[2], out length))
			{
				return null;
			}
			return new int[] { top, left, length };
		}


		static void Print(string label, List<string> fields)
		{
			if (fields.Count == 0)
			{
				return;
			}

			Console.WriteLine();
			Console.WriteLine("  " + label + ":");
			foreach (string field in fields.Take(40))
			{
				Console.WriteLine("    " + field);
			}
			if (fields.Count > 40)
			{
				Console.WriteLine("    ... and " + (fields.Count - 40) + " more");
			}
		}

		/// <summary>
		/// Reports small clusters that sit a few fields away from a much larger one. A signature
		/// seen once beside one seen hundreds of times is an anomalous instance of that screen,
		/// not a screen of its own - and saying so is cheaper than hand-diffing to find out.
		/// </summary>
		static void ReportOutliers(List<Cluster> clusters)
		{
			if (clusters.Count < 2)
			{
				return;
			}

			List<string> lines = new List<string>();

			foreach (Cluster small in clusters)
			{
				// Seen once or twice is the rarity test. There is no relative-size gate: the
				// search below only compares against strictly larger clusters, so a corpus where
				// everything is a singleton reports nothing, which is correct.
				if (small.Members.Count > 2)
				{
					continue;
				}

				HashSet<string> smallFields = new HashSet<string>(
					small.Signature.Split(';').Where(s => s.Length > 0));

				Cluster nearest = null;
				int nearestDistance = int.MaxValue;

				foreach (Cluster big in clusters)
				{
					if (ReferenceEquals(big, small) || big.Members.Count <= small.Members.Count)
					{
						continue;
					}

					HashSet<string> bigFields = new HashSet<string>(
						big.Signature.Split(';').Where(s => s.Length > 0));

					int distance = smallFields.Count(f => !bigFields.Contains(f))
						+ bigFields.Count(f => !smallFields.Contains(f));

					if (distance < nearestDistance)
					{
						nearestDistance = distance;
						nearest = big;
					}
				}

				if (nearest == null)
				{
					continue;
				}

				int fields = Math.Max(small.Members[0].FieldCount, 1);

				// A fifth of the fields is the line between "the same screen, drawn oddly" and
				// "a different screen that happens to resemble it".
				if (nearestDistance * 5 <= fields)
				{
					lines.Add("  [" + (clusters.IndexOf(small) + 1).ToString("D2") + "]  "
						+ small.Members.Count + " screen(s), differs from ["
						+ (clusters.IndexOf(nearest) + 1).ToString("D2") + "] ("
						+ nearest.Members.Count + " screens) by " + nearestDistance
						+ " of " + fields + " fields");
				}
			}

			if (lines.Count == 0)
			{
				return;
			}

			Console.WriteLine();
			Console.WriteLine("================ outliers ================");
			Console.WriteLine("  Seen once or twice, and within a fifth of a much larger cluster. These are");
			Console.WriteLine("  almost certainly anomalous instances of that screen rather than screens of");
			Console.WriteLine("  their own - one odd account, or a screen caught part way through a write.");
			Console.WriteLine("  A scored recognizer matches them with a degraded score, which is the point");
			Console.WriteLine("  of scoring. Do not give them nodes without reading the dump first.");
			Console.WriteLine();
			foreach (string line in lines)
			{
				Console.WriteLine(line);
			}
			Console.WriteLine();
			Console.WriteLine("  --diff <small> <large> shows exactly which fields, and the dump of a");
			Console.WriteLine("  single-member cluster is the anomaly itself.");
		}


		/// <summary>
		/// Compares clustering on geometry against clustering on the title row. Where a system
		/// names its screens in protected text, the title is both a cheaper and a more reliable
		/// key than a geometry hash - geometry that moves with the account makes a bucket lookup
		/// miss, whereas a title at fixed coordinates does not move at all.
		/// </summary>
		static void ReportTitles(List<CapturedScreen> formatted, List<Cluster> clusters)
		{
			var titled = formatted.Where(s => !string.IsNullOrEmpty(s.Title)).ToList();

			Console.WriteLine();
			Console.WriteLine("================ title row " + TitleRow
				+ (TitleLeft >= 0 ? ", columns " + TitleLeft + "-" + (TitleLeft + TitleLength - 1) : ", whole row")
				+ " ================");

			if (titled.Count == 0)
			{
				Console.WriteLine("  No protected text on row " + TitleRow
					+ ". Try --title-row with another row.");
				return;
			}

			var byTitle = titled.GroupBy(s => s.Title).OrderByDescending(g => g.Count()).ToList();

			Console.WriteLine("  screens with a title   " + titled.Count + " of " + formatted.Count);
			Console.WriteLine("  distinct titles        " + byTitle.Count);
			Console.WriteLine("  distinct signatures    " + clusters.Count);
			Console.WriteLine();

			if (byTitle.Count < clusters.Count)
			{
				Console.WriteLine("  The title collapses " + clusters.Count + " geometry clusters into "
					+ byTitle.Count + " screens. Geometry is");
				Console.WriteLine("  splitting screens that the title keeps together, which is H1 failing and");
				Console.WriteLine("  the title surviving. Prefer the title as the identification key and keep");
				Console.WriteLine("  geometry as a weak corroborating signal only.");
			}
			else if (byTitle.Count == clusters.Count)
			{
				Console.WriteLine("  The title and the geometry agree exactly. Either is usable as the key.");
			}
			else
			{
				Console.WriteLine("  More titles than signatures - the title is picking up data as well as");
				Console.WriteLine("  chrome, so it is not yet a key. Narrow it with --title-cols <left> <len>");
				Console.WriteLine("  until the count settles at the number of screens you actually visit.");
				Console.WriteLine();
				Console.WriteLine("  The per-cluster anchor candidates below are already the clean version of");
				Console.WriteLine("  this: protected text present in every member of a cluster and no other.");
				Console.WriteLine("  Read the top candidate of each cluster to find the columns to pin.");
			}

			// A title spanning several signatures is the direct evidence. A signature spanning
			// several titles is the opposite problem and worth knowing about.
			var split = byTitle
				.Where(g => g.Select(s => s.Signature).Distinct().Count() > 1)
				.ToList();

			if (split.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("  titles whose geometry wobbles:");
				foreach (var group in split.Take(20))
				{
					Console.WriteLine("    " + group.Select(s => s.Signature).Distinct().Count()
						+ " signature(s), " + group.Count() + " screen(s)   \"" + group.Key + "\"");
				}
				if (split.Count > 20)
				{
					Console.WriteLine("    ... and " + (split.Count - 20) + " more");
				}
			}

			var merged = clusters
				.Where(c => c.Members.Where(m => !string.IsNullOrEmpty(m.Title))
					.Select(m => m.Title).Distinct().Count() > 1)
				.ToList();

			if (merged.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("  signatures covering more than one title (geometry colliding screens):");
				foreach (Cluster cluster in merged.Take(10))
				{
					Console.WriteLine("    [" + (clusters.IndexOf(cluster) + 1).ToString("D2") + "]  "
						+ string.Join(" | ", cluster.Members.Select(m => m.Title).Distinct().Take(4)));
				}
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
