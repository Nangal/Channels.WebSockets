﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Networking;
using Microsoft.Extensions.Logging;
// borrowed from samples
namespace Channels.WebSockets
{
    internal class UvWriteReq2 : UvRequest
    {
        private readonly static Libuv.uv_write_cb _uv_write_cb = (IntPtr ptr, int status) => UvWriteCb(ptr, status);

        private IntPtr _bufs;

        private Action<UvWriteReq2, int, Exception, object> _callback;
        private object _state;
        private const int BUFFER_COUNT = 4;

        private List<GCHandle> _pins = new List<GCHandle>(BUFFER_COUNT + 1);

        public UvWriteReq2(IKestrelTrace logger) : base(logger)
        {
        }

        public void Init(UvLoopHandle loop)
        {
            var requestSize = loop.Libuv.req_size(Libuv.RequestType.WRITE);
            var bufferSize = Marshal.SizeOf<Libuv.uv_buf_t>() * BUFFER_COUNT;
            CreateMemory(
                loop.Libuv,
                loop.ThreadId,
                requestSize + bufferSize);
            _bufs = handle + requestSize;
        }

        public unsafe void Write(
            UvStreamHandle handle,
            ref ReadableBuffer buffer,
            Action<UvWriteReq2, int, Exception, object> callback,
            object state)
        {
            try
            {

                int nBuffers = 0;
                if (buffer.IsSingleSpan)
                {
                    nBuffers = 1;
                }
                else
                {
                    foreach (var span in buffer)
                    {
                        nBuffers++;
                    }
                }

                // add GCHandle to keeps this SafeHandle alive while request processing
                _pins.Add(GCHandle.Alloc(this, GCHandleType.Normal));

                var pBuffers = (Libuv.uv_buf_t*)_bufs;
                if (nBuffers > BUFFER_COUNT)
                {
                    // create and pin buffer array when it's larger than the pre-allocated one
                    var bufArray = new Libuv.uv_buf_t[nBuffers];
                    var gcHandle = GCHandle.Alloc(bufArray, GCHandleType.Pinned);
                    _pins.Add(gcHandle);
                    pBuffers = (Libuv.uv_buf_t*)gcHandle.AddrOfPinnedObject();
                }

                if (nBuffers == 1)
                {
                    pBuffers[0] = Libuv.buf_init(buffer.FirstSpan.BufferPtr, buffer.FirstSpan.Length);
                }
                else
                {
                    int i = 0;
                    foreach (var span in buffer)
                    {
                        pBuffers[i++] = Libuv.buf_init(span.BufferPtr, span.Length);
                    }
                }

                _callback = callback;
                _state = state;
                _uv.write(this, handle, pBuffers, nBuffers, _uv_write_cb);
            }
            catch
            {
                _callback = null;
                _state = null;
                Unpin(this);
                throw;
            }
        }

        private static void Unpin(UvWriteReq2 req)
        {
            foreach (var pin in req._pins)
            {
                pin.Free();
            }
            req._pins.Clear();
        }

        private static void UvWriteCb(IntPtr ptr, int status)
        {
            var req = FromIntPtr<UvWriteReq2>(ptr);
            Unpin(req);

            var callback = req._callback;
            req._callback = null;

            var state = req._state;
            req._state = null;

            Exception error = null;
            if (status < 0)
            {
                req.Libuv.Check(status, out error);
            }

            try
            {
                callback(req, status, error, state);
            }
            catch (Exception ex)
            {
                req._log.LogError(0, ex, "UvWriteCb");
                throw;
            }
        }
    }
}
