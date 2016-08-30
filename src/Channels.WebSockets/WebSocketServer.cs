﻿using Channels.Networking.Libuv;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Channels.WebSockets
{
    public class WebSocketServer : IDisposable
    {
        private UvTcpListener listener;
        private UvThread thread;
        private IPAddress ip;
        private int port;
        public int Port => port;
        public IPAddress IP => ip;

        public bool AllowClientsMissingConnectionHeaders { get; set; } = true; // stoopid browsers

        public WebSocketServer() : this(IPAddress.Any, 80) { }
        public WebSocketServer(IPAddress ip, int port)
        {
            this.ip = ip;
            this.port = port;
        }


        public void Dispose() => Dispose(true);
        ~WebSocketServer() { Dispose(false); }
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                GC.SuppressFinalize(this);
                Stop();
            }            
        }

        public void Start()
        {
            if (listener == null)
            {
                thread = new UvThread();
                listener = new UvTcpListener(thread, new IPEndPoint(ip, port));
                listener.OnConnection(async connection =>
                {
                    try
                    {
                        WriteStatus("Connected");
                        WebSocketConnection socket;
                        WriteStatus("Parsing http request...");
                        using (var request = await ParseHttpRequest(connection.Input))
                        {
                            WriteStatus("Identifying protocol...");
                            socket = GetProtocol(connection, request);
                            WriteStatus($"Protocol: {socket.WebSocketProtocol.Name}");
                            WriteStatus("Authenticating...");
                            if (!await OnAuthenticateAsync(socket)) throw new InvalidOperationException("Authentication refused");
                            WriteStatus("Completing handshake...");
                            await socket.WebSocketProtocol.CompleteHandshakeAsync(request, socket);
                        }
                        WriteStatus("Handshake complete hook...");
                        await OnHandshakeCompleteAsync(socket);
                        WriteStatus("Processing incoming frames...");
                        await socket.ProcessIncomingFramesAsync(this);
                    }
                    catch (Exception ex)
                    {// meh, bye bye broken connection
                        WriteStatus(ex.StackTrace);
                        WriteStatus(ex.GetType().Name);
                        WriteStatus(ex.Message);
                    }
                    finally
                    {
                        try { connection.Output.CompleteWriting(); } catch { }
                        try { connection.Input.CompleteReading(); } catch { }
                    }
                });
                listener.Start();
            }
        }

        [Conditional("DEBUG")]
        internal static void WriteStatus(string message)
        {
            Console.WriteLine($"[Server:{Environment.CurrentManagedThreadId}]: {message}");
        }

        public class WebSocketConnection
        {
            private UvTcpServerConnection connection;
            internal UvTcpServerConnection Connection => connection;
            internal WebSocketConnection(UvTcpServerConnection connection)
            {
                this.connection = connection;
            }

            public string Host { get; internal set; }
            public string Origin { get; internal set; }
            public string Protocol { get; internal set; }
            public string RequestLine { get; internal set; }
            internal WebSocketProtocol WebSocketProtocol { get; set; }

            internal async Task ProcessIncomingFramesAsync(WebSocketServer server)
            {
                ReadableBuffer buffer;
                bool keepReading;
                do
                {
                    buffer = await connection.Input;
                    keepReading = TryReadFrame(server, ref buffer);
                    buffer.Consumed(buffer.Start);
                }
                while (keepReading);
            }

            private bool TryReadFrame(WebSocketServer server, ref ReadableBuffer buffer)
            {
                if (buffer.IsEmpty && connection.Input.Completion.IsCompleted)
                {
                    return false; // that's all, folks
                }

                WebSocketsFrame frame;

                if (WebSocketProtocol.TryReadFrame(ref buffer, out frame))
                {
                    int payloadLength = frame.PayloadLength;
                    // buffer now points to the payload 
                    if (!frame.IsMasked)
                    {
                        throw new InvalidOperationException("Client-to-server frames should be masked");
                    }
                    if (frame.IsControlFrame && !frame.IsFinal)
                    {
                        throw new InvalidOperationException("Control frames cannot be fragmented");
                    }
                    server.OnFrameReceived(this, ref frame, ref buffer);
                    // and finally, progress past the frame
                    if (payloadLength != 0) buffer = buffer.Slice(payloadLength);
                }
                return true; // keep reading
            }
        }

        protected void OnFrameReceived(WebSocketConnection connection, ref WebSocketsFrame frame, ref ReadableBuffer buffer)
        {
            WriteStatus(frame.ToString());
            switch (frame.OpCode)
            {
                case WebSocketsFrame.OpCodes.Binary:
                    OnBinary(connection, ref frame, ref buffer);
                    break;
                case WebSocketsFrame.OpCodes.Close:
                    OnClose(connection, ref frame);
                    break;
                case WebSocketsFrame.OpCodes.Continuation:
                    OnContinuation(connection, ref frame, ref buffer);
                    break;
                case WebSocketsFrame.OpCodes.Ping:
                    OnPing(connection, ref frame);
                    break;
                case WebSocketsFrame.OpCodes.Pong:
                    OnPong(connection, ref frame);
                    break;
                case WebSocketsFrame.OpCodes.Text:
                    OnText(connection, ref frame, ref buffer);
                    break;
            }
        }

        protected virtual void OnPong(WebSocketConnection connection, ref WebSocketsFrame frame) { }
        protected virtual void OnPing(WebSocketConnection connection, ref WebSocketsFrame frame) { }
        protected virtual void OnClose(WebSocketConnection connection, ref WebSocketsFrame frame) { }
        protected virtual void OnContinuation(WebSocketConnection connection, ref WebSocketsFrame frame, ref ReadableBuffer buffer) { }
        protected virtual void OnBinary(WebSocketConnection connection, ref WebSocketsFrame frame, ref ReadableBuffer buffer) { }
        protected virtual void OnText(WebSocketConnection connection, ref WebSocketsFrame frame, ref ReadableBuffer buffer)
        {
            var handler = Text;
            if (handler != null)
            {
                frame.ApplyMask(ref buffer);
                handler(connection, buffer.GetUtf8String());
            }
        }
        public event Action<WebSocketConnection, string> Text;


        static readonly char[] Comma = { ',' };
        protected static class TaskResult
        {
            public static readonly Task<bool>
                True = Task.FromResult(true),
                False = Task.FromResult(false);
        }

        protected virtual Task<bool> OnAuthenticateAsync(WebSocketConnection connection) => TaskResult.True;
        protected virtual Task OnHandshakeCompleteAsync(WebSocketConnection connection) => TaskResult.True;

        private WebSocketConnection GetProtocol(UvTcpServerConnection connection, HttpRequest request)
        {
            var headers = request.Headers;
            string host = headers.GetAscii("Host");
            if (string.IsNullOrEmpty(host))
            {
                //4.   The request MUST contain a |Host| header field whose value
                //contains /host/ plus optionally ":" followed by /port/ (when not
                //using the default port).
                throw new InvalidOperationException("host required");
            }

            bool looksGoodEnough = false;
            // mozilla sends "keep-alive, Upgrade"; let's make it more forgiving
            var connectionParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (headers.ContainsKey("Connection"))
            {
                // so for mozilla, this will be the set {"keep-alive", "Upgrade"}
                var parts = headers.GetAscii("Connection").Split(Comma);
                foreach (var part in parts) connectionParts.Add(part.Trim());
            }
            if (connectionParts.Contains("Upgrade") && IsCaseInsensitiveAsciiMatch(headers.GetRaw("Upgrade"), "websocket"))
            {
                //5.   The request MUST contain an |Upgrade| header field whose value
                //MUST include the "websocket" keyword.
                //6.   The request MUST contain a |Connection| header field whose value
                //MUST include the "Upgrade" token.
                looksGoodEnough = true;
            }

            if (!looksGoodEnough && AllowClientsMissingConnectionHeaders)
            {
                if ((headers.ContainsKey("Sec-WebSocket-Version") && headers.ContainsKey("Sec-WebSocket-Key"))
                    || (headers.ContainsKey("Sec-WebSocket-Key1") && headers.ContainsKey("Sec-WebSocket-Key2")))
                {
                    looksGoodEnough = true;
                }
            }

            WebSocketProtocol protocol;
            if (looksGoodEnough)
            {
                //9.   The request MUST include a header field with the name
                //|Sec-WebSocket-Version|.  The value of this header field MUST be

                string version = headers.GetAscii("Sec-WebSocket-Version");
                if (version == null)
                {
                    if (headers.ContainsKey("Sec-WebSocket-Key1") && headers.ContainsKey("Sec-WebSocket-Key2"))
                    { // smells like hixie-76/hybi-00
                        protocol = WebSocketProtocol.Hixie76_00;
                    }
                    else
                    {
                        throw new NotSupportedException();
                    }
                }
                else
                {
                    switch (version)
                    {

                        case "4":
                        case "5":
                        case "6":
                        case "7":
                        case "8": // these are all early drafts
                        case "13": // this is later drafts and RFC6455
                            protocol = WebSocketProtocol.RFC6455_13;
                            break;
                        default:
                            // should issues a 400 "upgrade required" and specify Sec-WebSocket-Version - see 4.4
                            throw new InvalidOperationException(string.Format("Sec-WebSocket-Version {0} is not supported", version));
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("Request was not a web-socket upgrade request");
            }
            //The "Request-URI" of the GET method [RFC2616] is used to identify the
            //endpoint of the WebSocket connection, both to allow multiple domains
            //to be served from one IP address and to allow multiple WebSocket
            //endpoints to be served by a single server.
            var socket = new WebSocketConnection(connection);
            socket.Host = host;
            // Some early drafts used the latter, so we'll allow it as a fallback
            // in particular, two drafts of version "8" used (separately) **both**,
            // so we can't rely on the version for this (hybi-10 vs hybi-11).
            // To make it even worse, hybi-00 used Origin, so it is all over the place!
            socket.Origin = headers.GetAscii("Origin") ?? headers.GetAscii("Sec-WebSocket-Origin");
            socket.Protocol = headers.GetAscii("Sec-WebSocket-Protocol");
            socket.RequestLine = request.Path.GetAsciiString();
            socket.WebSocketProtocol = protocol;
            return socket;
        }
        public struct WebSocketsFrame
        {
            public override string ToString()
            {
                return OpCode.ToString() + ": " + PayloadLength.ToString()
                    + " bytes (" + Flags.ToString() + ", mask=" + Mask.ToString()
                    + ")";
            }
            private readonly byte header;
            private readonly byte header2;
            [Flags]
            public enum FrameFlags : byte
            {
                IsFinal = 128,
                Reserved1 = 64,
                Reserved2 = 32,
                Reserved3 = 16,
                None = 0
            }
            public enum OpCodes : byte
            {
                Continuation = 0,
                Text = 1,
                Binary = 2,
                // 3-7 reserved for non-control op-codes
                Close = 8,
                Ping = 9,
                Pong = 10,
                // 11-15 reserved for control op-codes
            }
            public WebSocketsFrame(byte header, bool isMasked, int mask, int payloadLength)
            {
                this.header = header;
                header2 = (byte)(isMasked ? 1 : 0);
                PayloadLength = payloadLength;
                Mask = mask;
            }
            public bool IsMasked => (header2 & 1) != 0;
            private bool HasFlag(FrameFlags flag) => (header & (byte)flag) != 0;

            internal unsafe void ApplyMask(ref ReadableBuffer buffer)
            {
                if (!IsMasked) return;
                ulong mask = (uint)Mask;
                if (mask == 0) return;
                mask = (mask << 32) | mask;

                foreach (var span in buffer)
                {
                    int len = span.Length;

                    if ((len & ~7) != 0) // >= 8
                    {
                        var ptr = (ulong*)span.BufferPtr;
                        do
                        {
                            (*ptr++) ^= mask;
                            len -= 8;
                        } while ((len & ~7) != 0); // >= 8
                    }
                    // TODO: worth doing an int32 mask here if >= 4?
                    if (len != 0)
                    {
                        var ptr = ((byte*)span.BufferPtr) + (buffer.Length & ~7); // forwards everything except the last chunk
                        do
                        {
                            var b = (byte)(mask & 255);
                            (*ptr++) ^= b;
                            // rotate the mask (need to preserve LHS in case we have another span)
                            mask = (mask >> 8) | (((ulong)b) << 56);
                            len--;
                        } while (len != 0);
                    }
                }
            }


            public bool IsControlFrame { get { return (header & (byte)OpCodes.Close) != 0; } }
            public int Mask { get; }
            public OpCodes OpCode => (OpCodes)(header & 15);
            public FrameFlags Flags => (FrameFlags)(header & ~15);
            public bool IsFinal { get { return HasFlag(FrameFlags.IsFinal); } }
            public bool Reserved1 { get { return HasFlag(FrameFlags.Reserved1); } }
            public bool Reserved2 { get { return HasFlag(FrameFlags.Reserved2); } }
            public bool Reserved3 { get { return HasFlag(FrameFlags.Reserved3); } }

            public int PayloadLength { get; }

        }
        internal abstract class WebSocketProtocol
        {
            internal static readonly WebSocketProtocol RFC6455_13 = new WebSocketProtocol_RFC6455_13(), Hixie76_00 = new WebSocketProtocol_Hixie76_00();

            public abstract string Name { get; }

            class WebSocketProtocol_RFC6455_13 : WebSocketProtocol
            {
                public override string Name => "RFC6455";
                static readonly byte[]
                    StandardPrefixBytes = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\n"
                                    + "Upgrade: websocket\r\n"
                                    + "Connection: Upgrade\r\n"
                                    + "Sec-WebSocket-Accept: "),
                    StandardPostfixBytes = Encoding.ASCII.GetBytes("\r\n\r\n");
                internal override Task CompleteHandshakeAsync(HttpRequest request, WebSocketConnection socket)
                {
                    var key = request.Headers.GetRaw("Sec-WebSocket-Key");

                    var connection = socket.Connection;

                    const int ResponseTokenLength = 28;
                    // how do I free this? do I need to?
                    var buffer = connection.Output.Alloc(StandardPrefixBytes.Length +
                        ResponseTokenLength + StandardPostfixBytes.Length);
                    string hashBase64 = ComputeReply(key, buffer.Memory);
                    if (hashBase64.Length != ResponseTokenLength) throw new InvalidOperationException("Unexpected response key length");
                    WebSocketServer.WriteStatus($"Response token: {hashBase64}");

                    buffer.Write(StandardPrefixBytes, 0, StandardPrefixBytes.Length);
                    buffer.CommitBytes(Encoding.ASCII.GetBytes(hashBase64, 0, hashBase64.Length, buffer.Memory.Array, buffer.Memory.Offset));
                    buffer.Write(StandardPostfixBytes, 0, StandardPostfixBytes.Length);

                    return buffer.FlushAsync();
                }
                static readonly byte[] WebSocketKeySuffixBytes = Encoding.ASCII.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11");

                static bool IsBase64(byte value)
                {
                    return (value >= (byte)'0' && value <= (byte)'9')
                        || (value >= (byte)'a' && value <= (byte)'z')
                        || (value >= (byte)'A' && value <= (byte)'Z')
                        || (value == (byte)'/')
                        || (value == (byte)'+')
                        || (value == (byte)'=');
                }
                internal static string ComputeReply(ReadableBuffer key, BufferSpan buffer)
                {
                    //To prove that the handshake was received, the server has to take two
                    //pieces of information and combine them to form a response.  The first
                    //piece of information comes from the |Sec-WebSocket-Key| header field
                    //in the client handshake:

                    //     Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==

                    //For this header field, the server has to take the value (as present
                    //in the header field, e.g., the base64-encoded [RFC4648] version minus
                    //any leading and trailing whitespace) and concatenate this with the
                    //Globally Unique Identifier (GUID, [RFC4122]) "258EAFA5-E914-47DA-
                    //95CA-C5AB0DC85B11" in string form, which is unlikely to be used by
                    //network endpoints that do not understand the WebSocket Protocol.  A
                    //SHA-1 hash (160 bits) [FIPS.180-3], base64-encoded (see Section 4 of
                    //[RFC4648]), of this concatenation is then returned in the server's
                    //handshake.

                    const int ExpectedKeyLength = 24;

                    int len = key.Length, start = 0, end = len, baseOffset = buffer.Offset;
                    if (len < ExpectedKeyLength) throw new ArgumentException("Undersized key", nameof(key));
                    byte[] arr = buffer.Array;
                    // note that it might be padded; if so we'll need to trim - allow some slack
                    if ((len + WebSocketKeySuffixBytes.Length) > buffer.Length) throw new ArgumentException("Oversized key", nameof(key));
                    // in-place "trim" to find the base-64 piece
                    key.CopyTo(arr, baseOffset);
                    for (int i = 0; i < len; i++)
                    {
                        if (IsBase64(arr[baseOffset + i])) break;
                        start++;
                    }
                    for (int i = len - 1; i >= 0; i--)
                    {
                        if (IsBase64(arr[baseOffset + i])) break;
                        end--;
                    }

                    if ((end - start) != ExpectedKeyLength) throw new ArgumentException(nameof(key));

                    // append the suffix
                    Buffer.BlockCopy(WebSocketKeySuffixBytes, 0, arr, baseOffset + end, WebSocketKeySuffixBytes.Length);

                    // compute the hash
                    using (var sha = SHA1.Create())
                    {
                        var hash = sha.ComputeHash(arr, baseOffset + start,
                            ExpectedKeyLength + WebSocketKeySuffixBytes.Length);
                        return Convert.ToBase64String(hash);
                    }
                }
                protected internal static unsafe int ReadBigEndianInt32(byte* buffer, int offset)
                {
                    return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
                }
                protected internal static unsafe int ReadLittleEndianInt32(byte* buffer, int offset)
                {
                    return (buffer[offset]) | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
                }
                internal unsafe override bool TryReadFrame(ref ReadableBuffer buffer, out WebSocketsFrame frame)
                {
                    frame = default(WebSocketsFrame);
                    int payloadLength, bytesAvailable = buffer.Length;
                    if (bytesAvailable < 2) return false; // can't read that; frame takes at minimum two bytes

                    // header is at most 14 bytes; can afford the stack for that - but note that if we aim for 16 bytes instead,
                    // we will usually benefit from using 2 qword copies (handled internally); very very small messages ('a') might
                    // have to use the slower version, but... meh
                    byte* header = stackalloc byte[16];
                    SlowCopyFirst(buffer, header, 16);

                    bool masked = (header[1] & 128) != 0;
                    int tmp = header[1] & 127;
                    int headerLength, maskOffset;
                    switch (tmp)
                    {
                        case 126:
                            headerLength = masked ? 8 : 4;
                            if (bytesAvailable < headerLength) return false;
                            payloadLength = (header[2] << 8) | header[3];
                            maskOffset = 4;
                            break;
                        case 127:
                            headerLength = masked ? 14 : 10;
                            if (bytesAvailable < headerLength) return false;
                            int big = ReadBigEndianInt32(header, 2), little = ReadBigEndianInt32(header, 6);
                            if (big != 0 || little < 0) throw new ArgumentOutOfRangeException(); // seriously, we're not going > 2GB
                            payloadLength = little;
                            maskOffset = 10;
                            break;
                        default:
                            headerLength = masked ? 6 : 2;
                            if (bytesAvailable < headerLength) return false;
                            payloadLength = tmp;
                            maskOffset = 2;
                            break;
                    }
                    if (bytesAvailable < headerLength + payloadLength) return false; // body isn't intact


                    frame = new WebSocketsFrame(header[0], masked,
                        masked ? ReadLittleEndianInt32(header, maskOffset) : 0,
                        payloadLength);
                    buffer = buffer.Slice(headerLength); // header is fully consumed now
                    return true;
                }

                private unsafe uint SlowCopyFirst(ReadableBuffer buffer, byte* destination, uint bytes)
                {
                    if (bytes == 0) return 0;
                    if (buffer.IsSingleSpan)
                    {
                        var span = buffer.FirstSpan;
                        uint batch = Math.Min((uint)span.Length, bytes);
                        Copy((byte*)span.BufferPtr, destination, batch);
                        return batch;
                    }
                    else
                    {
                        uint copied = 0;
                        foreach (var span in buffer)
                        {
                            uint batch = Math.Min((uint)span.Length, bytes);
                            Copy((byte*)span.BufferPtr, destination, batch);
                            destination += batch;
                            copied += batch;
                            bytes -= batch;
                            if (bytes == 0) break;
                        }
                        return copied;
                    }
                }
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static unsafe void Copy(byte* source, byte* destination, uint bytes)
            {
                if ((bytes & ~7) != 0) // >= 8
                {
                    ulong* source8 = (ulong*)source, destination8 = (ulong*)destination;
                    do
                    {
                        *destination8++ = *source8++;
                        bytes -= 8;
                    } while ((bytes & ~7) != 0); // >= 8
                    source = (byte*)source8;
                    destination = (byte*)destination8;
                }
                while (bytes-- != 0)
                {
                    *destination++ = *source++;
                }
            }
            class WebSocketProtocol_Hixie76_00 : WebSocketProtocol
            {
                public override string Name => "Hixie76";
                internal override Task CompleteHandshakeAsync(HttpRequest request, WebSocketConnection socket)
                {
                    throw new NotImplementedException();
                }
                internal override bool TryReadFrame(ref ReadableBuffer buffer, out WebSocketsFrame frame)
                {
                    throw new NotImplementedException();
                }
            }

            internal abstract Task CompleteHandshakeAsync(HttpRequest request, WebSocketConnection socket);

            internal abstract bool TryReadFrame(ref ReadableBuffer buffer, out WebSocketsFrame frame);
        }

        internal struct HttpRequest : IDisposable
        {
            public void Dispose()
            {
                Method.Dispose();
                Path.Dispose();
                HttpVersion.Dispose();
                Headers.Dispose();
                Method = Path = HttpVersion = default(ReadableBuffer);
                Headers = default(HttpRequestHeaders);
            }
            public ReadableBuffer Method { get; private set; }
            public ReadableBuffer Path { get; private set; }
            public ReadableBuffer HttpVersion { get; private set; }

            public HttpRequestHeaders Headers { get; private set; }

            public HttpRequest(ReadableBuffer method, ReadableBuffer path, ReadableBuffer httpVersion, Dictionary<string, ReadableBuffer> headers)
            {
                Method = method;
                Path = path;
                HttpVersion = httpVersion;
                Headers = new HttpRequestHeaders(headers);
            }
        }
        internal struct HttpRequestHeaders : IEnumerable<KeyValuePair<string, ReadableBuffer>>, IDisposable
        {
            private Dictionary<string, ReadableBuffer> headers;
            public void Dispose()
            {
                if (headers != null)
                {
                    foreach (var pair in headers)
                        pair.Value.Dispose();
                }
                headers = null;
            }
            public HttpRequestHeaders(Dictionary<string, ReadableBuffer> headers)
            {
                this.headers = headers;
            }
            public bool ContainsKey(string key) => headers.ContainsKey(key);
            IEnumerator<KeyValuePair<string, ReadableBuffer>> IEnumerable<KeyValuePair<string, ReadableBuffer>>.GetEnumerator() => ((IEnumerable<KeyValuePair<string, ReadableBuffer>>)headers).GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)headers).GetEnumerator();
            public Dictionary<string, ReadableBuffer>.Enumerator GetEnumerator() => headers.GetEnumerator();

            public string GetAscii(string key)
            {
                ReadableBuffer buffer;
                if (headers.TryGetValue(key, out buffer)) return buffer.GetAsciiString();
                return null;
            }
            public ReadableBuffer GetRaw(string key)
            {
                ReadableBuffer buffer;
                if (headers.TryGetValue(key, out buffer)) return buffer;
                return default(ReadableBuffer);
            }

        }
        private enum ParsingState
        {
            StartLine,
            Headers
        }

        private static Vector<byte>
            _vectorCRs = new Vector<byte>((byte)'\r'),
            _vectorLFs = new Vector<byte>((byte)'\n'),
            _vectorSpaces = new Vector<byte>((byte)' '),
            _vectorColons = new Vector<byte>((byte)':');
        private static async Task<HttpRequest> ParseHttpRequest(IReadableChannel _input)
        {
            ReadableBuffer Method = default(ReadableBuffer), Path = default(ReadableBuffer), HttpVersion = default(ReadableBuffer);
            Dictionary<string, ReadableBuffer> Headers = new Dictionary<string, ReadableBuffer>();
            try
            {
                ParsingState _state = ParsingState.StartLine;
                bool needMoreData = true;
                while (needMoreData)
                {
                    var buffer = await _input;

                    var consumed = buffer.Start;
                    needMoreData = true;

                    try
                    {
                        if (buffer.IsEmpty && _input.Completion.IsCompleted)
                        {
                            throw new EndOfStreamException();
                        }

                        if (_state == ParsingState.StartLine)
                        {
                            // Find \n
                            var delim = buffer.IndexOf(ref _vectorLFs);
                            if (delim.IsEnd)
                            {
                                continue;
                            }

                            // Grab the entire start line
                            var startLine = buffer.Slice(0, delim);

                            // Move the buffer to the rest
                            buffer = buffer.Slice(delim).Slice(1);

                            delim = startLine.IndexOf(ref _vectorSpaces);
                            if (delim.IsEnd)
                            {
                                throw new Exception();
                            }

                            var method = startLine.Slice(0, delim);
                            Method = method.Clone();

                            // Skip ' '
                            startLine = startLine.Slice(delim).Slice(1);

                            delim = startLine.IndexOf(ref _vectorSpaces);
                            if (delim.IsEnd)
                            {
                                throw new Exception();
                            }

                            var path = startLine.Slice(0, delim);
                            Path = path.Clone();

                            // Skip ' '
                            startLine = startLine.Slice(delim).Slice(1);

                            delim = startLine.IndexOf(ref _vectorCRs);
                            if (delim.IsEnd)
                            {
                                throw new Exception();
                            }

                            var httpVersion = startLine.Slice(0, delim);
                            HttpVersion = httpVersion.Clone();

                            _state = ParsingState.Headers;
                            consumed = startLine.End;
                        }

                        // Parse headers
                        // key: value\r\n

                        while (!buffer.IsEmpty)
                        {
                            var ch = buffer.Peek();

                            if (ch == -1)
                            {
                                break;
                            }

                            if (ch == '\r')
                            {
                                // Check for final CRLF.
                                buffer = buffer.Slice(1);
                                ch = buffer.Peek();
                                buffer = buffer.Slice(1);

                                if (ch == -1)
                                {
                                    break;
                                }
                                else if (ch == '\n')
                                {
                                    consumed = buffer.Start;
                                    needMoreData = false;
                                    break;
                                }

                                // Headers don't end in CRLF line.
                                throw new Exception();
                            }

                            var headerName = default(ReadableBuffer);
                            var headerValue = default(ReadableBuffer);

                            // End of the header
                            // \n
                            var delim = buffer.IndexOf(ref _vectorLFs);
                            if (delim.IsEnd)
                            {
                                break;
                            }

                            var headerPair = buffer.Slice(0, delim);
                            buffer = buffer.Slice(delim).Slice(1);

                            // :
                            delim = headerPair.IndexOf(ref _vectorColons);
                            if (delim.IsEnd)
                            {
                                throw new Exception();
                            }

                            headerName = headerPair.Slice(0, delim).TrimStart();
                            headerPair = headerPair.Slice(delim).Slice(1);

                            // \r
                            delim = headerPair.IndexOf(ref _vectorCRs);
                            if (delim.IsEnd)
                            {
                                // Bad request
                                throw new Exception();
                            }

                            headerValue = headerPair.Slice(0, delim).TrimStart();

                            Headers[ToHeaderKey(ref headerName)] = headerValue.Clone();

                            // Move the consumed
                            consumed = buffer.Start;
                        }
                    }
                    finally
                    {
                        buffer.Consumed(consumed);
                    }
                }
                var result = new HttpRequest(Method, Path, HttpVersion, Headers);
                Method = Path = HttpVersion = default(ReadableBuffer);
                Headers = null;
                return result;
            }
            finally
            {
                Method.Dispose();
                Path.Dispose();
                HttpVersion.Dispose();
                if (Headers != null)
                {
                    foreach (var pair in Headers)
                        pair.Value.Dispose();
                }
            }
        }

        static readonly string[] CommonHeaders = new string[]
        {
            "Accept",
            "Accept-Encoding",
            "Accept-Language",
            "Cache-Control",
            "Connection",
            "Cookie",
            "Host",
            "Origin",
            "Pragma",
            "Sec-WebSocket-Extensions",
            "Sec-WebSocket-Key",
            "Sec-WebSocket-Key1",
            "Sec-WebSocket-Key2",
            "Sec-WebSocket-Origin",
            "Sec-WebSocket-Version",
            "Upgrade",
            "Upgrade-Insecure-Requests",
            "User-Agent"
        }, CommonHeadersLowerCaseInvariant = CommonHeaders.Select(s => s.ToLowerInvariant()).ToArray();
        private static string ToHeaderKey(ref ReadableBuffer headerName)
        {
            var lowerCaseHeaders = CommonHeadersLowerCaseInvariant;
            for (int i = 0; i < lowerCaseHeaders.Length; i++)
            {
                if (IsCaseInsensitiveAsciiMatch(headerName, lowerCaseHeaders[i])) return CommonHeaders[i];
            }

            return headerName.GetAsciiString();
        }

        private static unsafe bool IsCaseInsensitiveAsciiMatch(ReadableBuffer bufferUnknownCase, string valueLowerCase)
        {
            if (bufferUnknownCase.Length != valueLowerCase.Length) return false;
            int index = 0;
            fixed (char* valuePtr = valueLowerCase)
                foreach (var span in bufferUnknownCase)
                {
                    byte* bufferPtr = (byte*)span.BufferPtr;
                    for (int i = 0; i < span.Length; i++)
                    {
                        char x = (char)(*bufferPtr++), y = valuePtr[index++];
                        if (x != y && char.ToLowerInvariant(x) != y) return false;
                    }
                }
            return true;
        }

        public void Stop()
        {
            listener?.Stop();
            thread?.Dispose();
            listener = null;
            thread = null;
        }
    }
}
