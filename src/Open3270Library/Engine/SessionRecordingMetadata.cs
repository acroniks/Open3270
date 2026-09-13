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

namespace Open3270
{
	/// <summary>
	/// Everything about a recording that the replay log format cannot carry.
	/// </summary>
	/// <remarks>
	/// The log format has no comment syntax - its parser calls Convert.ToInt32 on the first six
	/// characters of every line before it looks at anything else - so metadata has to travel in a
	/// sidecar file written alongside the log.
	/// <para>
	/// Nothing here should identify a credential. <see cref="Target"/> is an opaque id whose meaning
	/// lives in the recording environment's own configuration.
	/// </para>
	/// </remarks>
	public class SessionRecordingMetadata
	{
		/// <summary>
		/// Schema version of the sidecar. Incremented when a field changes meaning.
		/// </summary>
		public int FormatVersion = 1;

		/// <summary>
		/// Identifies this recording. Defaults to the log file's base name.
		/// </summary>
		public string RecordingId;

		/// <summary>
		/// Opaque id of the target this session was recorded against. Never a credential.
		/// </summary>
		public string Target;

		/// <summary>
		/// Opaque id of the layout set this target maps to.
		/// </summary>
		public string Variant;

		/// <summary>
		/// Terminal type as configured, for example "IBM-3278-2".
		/// </summary>
		public string TermType;

		/// <summary>
		/// Negotiated screen width in columns. Set from the connected session, never assumed:
		/// models 2 to 5 are 80, 80, 80 and 132 columns.
		/// </summary>
		public int Columns;

		/// <summary>
		/// Negotiated screen height in rows. Models 2 to 5 are 24, 32, 43 and 27 rows.
		/// </summary>
		public int Rows;

		/// <summary>
		/// Commit of the engine that produced the recording, so a replay can be reproduced against
		/// the code that wrote it.
		/// </summary>
		public string EngineCommit;

		/// <summary>
		/// What produced this recording, for example "passive-collector".
		/// </summary>
		public string CapturedBy;

		/// <summary>
		/// Whether any redaction pass has been applied. "none" for a freshly captured recording.
		/// </summary>
		public string Redaction = "none";

		/// <summary>
		/// UTC time the recording started. Set by the recorder.
		/// </summary>
		public DateTime CapturedAtUtc;

		/// <summary>
		/// Action tags in dispatch order, timestamp aligned to the log.
		/// </summary>
		public List<SessionRecordingKeystroke> Keystrokes = new List<SessionRecordingKeystroke>();
	}

	/// <summary>
	/// One action dispatched during a recording. Carries the tag and a length, never the text.
	/// </summary>
	public class SessionRecordingKeystroke
	{
		/// <summary>
		/// HHMMSS, matching the timestamp column of the log lines around it.
		/// </summary>
		public string Time;

		/// <summary>
		/// Action name, lower cased, with the key number appended for PF and PA keys.
		/// </summary>
		public string Tag;

		/// <summary>
		/// Character count for actions carrying text, otherwise zero.
		/// </summary>
		public int Length;
	}
}
