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

namespace Open3270
{
	/// <summary>
	/// Receives the raw wire bytes of a session in both directions, plus a tag for each action
	/// dispatched by name, so that the session can be replayed later through
	/// <see cref="ConnectionConfig.LogFile"/>.
	/// </summary>
	/// <remarks>
	/// This is the write counterpart to <see cref="ConnectionConfig.LogFile"/>, which is read-only.
	/// It is an interface rather than a TextWriter so that rotation, redaction and alternative
	/// stores have somewhere to live without changing the call sites in Telnet.
	/// <para>
	/// Implementations are called from two different threads: <see cref="HostToClient"/> runs on
	/// the socket receive thread and <see cref="ClientToHost"/> on whichever thread keyed the data.
	/// An implementation must therefore be thread safe on its own account, must not block (the
	/// receive thread stalls ingest for the whole session if it does), and must never take a lock
	/// that Telnet itself holds.
	/// </para>
	/// <para>
	/// Recordings are raw wire bytes. Host output is cleartext EBCDIC, which is not encryption, and
	/// keyed input includes whatever was typed into non-display fields. Treat what an implementation
	/// writes as sensitive.
	/// </para>
	/// </remarks>
	public interface ISessionRecorder : IDisposable
	{
		/// <summary>
		/// Records bytes received from the host, before the telnet state machine consumes them.
		/// </summary>
		/// <param name="buffer">Receive buffer. Only the first <paramref name="length"/> bytes are valid.</param>
		/// <param name="length">Number of valid bytes in <paramref name="buffer"/>.</param>
		void HostToClient(byte[] buffer, int length);

		/// <summary>
		/// Records bytes sent to the host, as they leave for the socket.
		/// </summary>
		/// <param name="buffer">Output buffer. Only the first <paramref name="length"/> bytes are valid.</param>
		/// <param name="length">Number of valid bytes in <paramref name="buffer"/>.</param>
		void ClientToHost(byte[] buffer, int length);

		/// <summary>
		/// Records that an action was dispatched, by name.
		/// </summary>
		/// <param name="tag">
		/// The action name, lower cased, with the key number appended for PF and PA keys - so
		/// "enter", "pf3", "string".
		/// </param>
		/// <param name="length">
		/// For actions carrying text, the number of characters. The text itself is deliberately not
		/// passed: the text is the data.
		/// </param>
		void Keystroke(string tag, int length);
	}
}
