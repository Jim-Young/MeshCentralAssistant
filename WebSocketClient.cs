﻿/*
Copyright 2009-2021 Intel Corporation

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Net.Sockets;
using System.Net.Security;
using System.IO.Compression;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace MeshAssistant
{
    public class webSocketClient : IDisposable
    {
        public bool AllowCompression = true;
        private TcpClient wsclient = null;
        private SslStream wsstream = null;
        private NetworkStream wsrawstream = null;
        private ConnectionStates state = 0;
        private int fragmentParsingState = 0;
        private Uri url = null;
        private byte[] readBuffer = new Byte[1024];
        private int readBufferLen = 0;
        private int accopcodes = 0;
        private bool accmask = false;
        private int acclen = 0;
        private bool proxyInUse = false;
        private string tlsCertFingerprint = null;
        //private ConnectionErrors lastError = ConnectionErrors.NoError;
        public bool xdebug = false;
        public bool xignoreCert = false;
        public string extraHeaders = null;
        private MemoryStream inflateMemory;
        private DeflateStream inflate;
        private MemoryStream deflateMemory;
        private static byte[] inflateEnd = { 0x00, 0x00, 0xff, 0xff };
        private static byte[] inflateStart = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        public int pingTimeSeconds = 0;
        public int pongTimeSeconds = 0;
        private System.Threading.Timer pingTimer = null;
        private System.Threading.Timer pongTimer = null;
        private bool pendingSendCall = false;
        private MemoryStream pendingSendBuffer = null;
        public long PendingSendLength { get { return (pendingSendBuffer == null)? 0 : pendingSendBuffer.Length; } }
        private bool readPaused = false;
        private bool shouldRead = false;

        // Outside variables
        public object tag = null;
        public int id = 0;
        public bool tunneling = false;
        public IPEndPoint endpoint;

        public enum ConnectionStates
        {
            Disconnected = 0,
            Connecting = 1,
            Connected = 2
        }

        public enum ConnectionErrors
        {
            NoError = 0
        }

        public delegate void onBinaryDataHandler(webSocketClient sender, byte[] data, int offset, int length, int orglen);
        public event onBinaryDataHandler onBinaryData;
        public delegate void onStringDataHandler(webSocketClient sender, string data, int orglen);
        public event onStringDataHandler onStringData;
        public delegate void onDebugMessageHandler(webSocketClient sender, string msg);
        public event onDebugMessageHandler onDebugMessage;
        public delegate void onStateChangedHandler(webSocketClient sender, ConnectionStates state);
        public event onStateChangedHandler onStateChanged;
        public delegate void onSendOkHandler(webSocketClient sender);
        public event onSendOkHandler onSendOk;

        public ConnectionStates State { get { return state; } }

        public X509Certificate RemoteCertificate { get { return wsstream.RemoteCertificate; } }

        private void SetState(ConnectionStates newstate)
        {
            if (state == newstate) return;
            state = newstate;
            if (onStateChanged != null) { onStateChanged(this, state); }
        }

        public void Dispose()
        {
            if (pingTimer != null) { pingTimer.Dispose(); pingTimer = null; }
            if (pongTimer != null) { pongTimer.Dispose(); pongTimer = null; }
            if (wsstream != null) { try { wsstream.Close(); } catch (Exception) { } try { wsstream.Dispose(); } catch (Exception) { } wsstream = null; }
            if (wsclient != null) { wsclient = null; }
            if (pendingSendBuffer != null) { pendingSendBuffer.Dispose(); pendingSendBuffer = null; }
            pendingSendCall = false;
            SetState(ConnectionStates.Disconnected);
        }

        public void Debug(string msg) { if (onDebugMessage != null) { onDebugMessage(this, msg); } if (xdebug) { try { File.AppendAllText("debug.log", "Debug-" + msg + "\r\n"); } catch (Exception) { } } }

        public bool Start(Uri url, string tlsCertFingerprint)
        {
            if (state != ConnectionStates.Disconnected) return false;
            SetState(ConnectionStates.Connecting);
            this.url = url;
            if (tlsCertFingerprint != null) { this.tlsCertFingerprint = tlsCertFingerprint.ToUpper(); }
            Uri proxyUri = null;

            Debug("Websocket Start, URL=" + url.ToString());

            // Check if we need to use a HTTP proxy (Auto-proxy way)
            try
            {
                RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
                Object x = registryKey.GetValue("AutoConfigURL", null);
                if ((x != null) && (x.GetType() == typeof(string)))
                {
                    string proxyStr = GetProxyForUrlUsingPac("http" + ((url.Port == 80) ? "" : "s") + "://" + url.Host + ":" + url.Port, x.ToString());
                    if (proxyStr != null) { proxyUri = new Uri("http://" + proxyStr); }
                }
            }
            catch (Exception) { proxyUri = null; }

            // Check if we need to use a HTTP proxy (Normal way)
            if (proxyUri == null)
            {
                var proxy = System.Net.HttpWebRequest.GetSystemWebProxy();
                proxyUri = proxy.GetProxy(url);
                if ((url.Host.ToLower() == proxyUri.Host.ToLower()) && (url.Port == proxyUri.Port)) { proxyUri = null; }
            }

            if (proxyUri != null)
            {
                // Proxy in use
                Debug("Websocket proxyUri: " + proxyUri.ToString());
                proxyInUse = true;
                wsclient = new TcpClient();
                wsclient.BeginConnect(proxyUri.Host, proxyUri.Port, new AsyncCallback(OnConnectSink), this);
            }
            else
            {
                // No proxy in use
                Debug("Websocket noProxy");
                proxyInUse = false;
                wsclient = new TcpClient();
                wsclient.BeginConnect(url.Host, url.Port, new AsyncCallback(OnConnectSink), this);
            }

            return true;
        }

        private void OnConnectSink(IAsyncResult ar)
        {
            if (wsclient == null) return;

            // Accept the connection
            try
            {
                wsclient.EndConnect(ar);
            }
            catch (Exception ex)
            {
                Debug("Websocket TCP failed to connect: " + ex.ToString());
                Dispose();
                return;
            }

            if (proxyInUse == true)
            {
                // Send proxy connection request
                wsrawstream = wsclient.GetStream();
                byte[] proxyRequestBuf = UTF8Encoding.UTF8.GetBytes("CONNECT " + url.Host + ":" + url.Port + " HTTP/1.1\r\nHost: " + url.Host + ":" + url.Port + "\r\n\r\n");
                wsrawstream.Write(proxyRequestBuf, 0, proxyRequestBuf.Length);
                wsrawstream.BeginRead(readBuffer, readBufferLen, readBuffer.Length - readBufferLen, new AsyncCallback(OnProxyResponseSink), this);
            }
            else
            {
                // Start TLS connection
                Debug("Websocket TCP connected, doing TLS...");
                wsstream = new SslStream(wsclient.GetStream(), false, VerifyServerCertificate, null);
                wsstream.BeginAuthenticateAsClient(url.Host, null, System.Security.Authentication.SslProtocols.Tls12, false, new AsyncCallback(OnTlsSetupSink), this);
            }
        }

        private void OnProxyResponseSink(IAsyncResult ar)
        {
            if (wsrawstream == null) return;

            int len = 0;
            try { len = wsrawstream.EndRead(ar); } catch (Exception) { }
            if (len == 0)
            {
                // Disconnect
                Debug("Websocket proxy disconnected, length = 0.");
                Dispose();
                return;
            }

            readBufferLen += len;
            string proxyResponse = UTF8Encoding.UTF8.GetString(readBuffer, 0, readBufferLen);
            if (proxyResponse.IndexOf("\r\n\r\n") >= 0)
            {
                // We get a full proxy response, we should get something like "HTTP/1.1 200 Connection established\r\n\r\n"
                if (proxyResponse.StartsWith("HTTP/1.1 200 "))
                {
                    // All good, start TLS setup.
                    readBufferLen = 0;
                    Debug("Websocket TCP connected, doing TLS...");
                    wsstream = new SslStream(wsrawstream, false, VerifyServerCertificate, null);
                    wsstream.BeginAuthenticateAsClient(url.Host, null, System.Security.Authentication.SslProtocols.Tls12, false, new AsyncCallback(OnTlsSetupSink), this);
                }
                else
                {
                    // Invalid response
                    Debug("Proxy connection failed: " + proxyResponse);
                    Dispose();
                }
            }
            else
            {
                if (readBufferLen == readBuffer.Length)
                {
                    // Buffer overflow
                    Debug("Proxy connection failed");
                    Dispose();
                }
                else
                {
                    // Read more proxy data
                    wsrawstream.BeginRead(readBuffer, readBufferLen, readBuffer.Length - readBufferLen, new AsyncCallback(OnProxyResponseSink), this);
                }
            }
        }

        public string Base64Encode(string plainText)
        {
            var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
            return System.Convert.ToBase64String(plainTextBytes);
        }

        public string Base64Decode(string base64EncodedData)
        {
            var base64EncodedBytes = System.Convert.FromBase64String(base64EncodedData);
            return System.Text.Encoding.UTF8.GetString(base64EncodedBytes);
        }

        private void OnTlsSetupSink(IAsyncResult ar)
        {
            if (wsstream == null) return;

            // Accept the connection
            try
            {
                wsstream.EndAuthenticateAsClient(ar);
            }
            catch (Exception ex)
            {
                // Disconnect
                Debug("Websocket TLS failed: " + ex.ToString());
                Dispose();
                return;
            }

            pendingSendBuffer = new MemoryStream();
            pendingSendCall = false;

            // Send the HTTP headers
            Debug("Websocket TLS setup, sending HTTP header...");
            string header;
            if (AllowCompression) {
                header = "GET " + url.PathAndQuery + " HTTP/1.1\r\nHost: " + url.Host + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Extensions: permessage-deflate; client_no_context_takeover\r\n" + extraHeaders + "\r\n";
            } else {
                header = "GET " + url.PathAndQuery + " HTTP/1.1\r\nHost: " + url.Host + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n" + extraHeaders + "\r\n";
            }
            SendData(UTF8Encoding.UTF8.GetBytes(header));

            // Start receiving data
            wsstream.BeginRead(readBuffer, readBufferLen, readBuffer.Length - readBufferLen, new AsyncCallback(OnTlsDataSink), this);
        }

        private void OnTlsDataSink(IAsyncResult ar)
        {
            if (wsstream == null) return;

            int len = 0;
            try { len = wsstream.EndRead(ar); } catch (Exception) { }
            if (len == 0)
            {
                // Disconnect
                Debug("Websocket disconnected, length = 0.");
                Dispose();
                return;
            }
            //parent.Debug("#" + counter + ": Websocket got new data: " + len);
            readBufferLen += len;

            // Consume all of the data
            int consumed = 0;
            int ptr = 0;
            do
            {
                consumed = ProcessBuffer(readBuffer, ptr, readBufferLen - ptr);
                if (consumed < 0) { Dispose(); return; } // Error, close the connection
                ptr += consumed;
            } while ((consumed > 0) && ((readBufferLen - consumed) > 0));

            // Move the data forward
            if ((ptr > 0) && (readBufferLen - ptr) > 0)
            {
                //Console.Write("MOVE FORWARD\r\n");
                Array.Copy(readBuffer, ptr, readBuffer, 0, (readBufferLen - ptr));
            }
            readBufferLen = (readBufferLen - ptr);

            // If the buffer is too small, double the size here.
            if (readBuffer.Length - readBufferLen == 0)
            {
                Debug("Increasing the read buffer size from " + readBuffer.Length + " to " + (readBuffer.Length * 2) + ".");
                byte[] readBuffer2 = new byte[readBuffer.Length * 2];
                Array.Copy(readBuffer, 0, readBuffer2, 0, readBuffer.Length);
                readBuffer = readBuffer2;
            }

            // Receive more data
            if (readPaused == false)
            {
                try { wsstream.BeginRead(readBuffer, readBufferLen, readBuffer.Length - readBufferLen, new AsyncCallback(OnTlsDataSink), this); } catch (Exception) { }
            }
            else
            {
                shouldRead = true;
            }
        }
        private void WriteWebSocketAsyncDone(IAsyncResult ar)
        {
            if ((wsstream == null) || (pendingSendBuffer == null)) return;
            try { wsstream.EndWrite(ar); } catch (Exception) { }
            if (pendingSendBuffer == null) return;
            lock (pendingSendBuffer)
            {
                if (pendingSendBuffer == null) return;
                if (pendingSendBuffer.Length > 0)
                {
                    byte[] buf = pendingSendBuffer.ToArray();
                    try { wsstream.BeginWrite(buf, 0, buf.Length, new AsyncCallback(WriteWebSocketAsyncDone), null); } catch (Exception) { Dispose(); return; }
                    pendingSendBuffer.SetLength(0);
                }
                else
                {
                    pendingSendCall = false;
                    if (onSendOk != null) { onSendOk(this); }
                }
            }
        }

        private void PingTimerCallback(object state) { SendPing(null, 0, 0); }

        private void PongTimerCallback(object state) { SendPong(null, 0, 0); }

        private int ProcessBuffer(byte[] buffer, int offset, int len)
        {
            string ss = UTF8Encoding.UTF8.GetString(buffer, offset, len);

            if (state == ConnectionStates.Connecting)
            {
                // Look for the end of the http header
                string header = UTF8Encoding.UTF8.GetString(buffer, offset, len);
                int i = header.IndexOf("\r\n\r\n");
                if (i == -1) return 0;
                Dictionary<string, string> parsedHeader = ParseHttpHeader(header.Substring(0, i));
                if ((parsedHeader == null) || (parsedHeader["_Path"] != "101")) { Debug("Websocket bad header."); return -1; } // Bad header, close the connection
                Debug("Websocket got setup upgrade header.");
                SetState(ConnectionStates.Connected);

                if (parsedHeader.ContainsKey("sec-websocket-extensions") && (parsedHeader["sec-websocket-extensions"].IndexOf("permessage-deflate") >= 0))
                {
                    inflateMemory = new MemoryStream();
                    inflate = new DeflateStream(inflateMemory, CompressionMode.Decompress);
                    deflateMemory = new MemoryStream();
                }

                // Start ping/pong timers if needed
                if (pingTimeSeconds > 0) { pingTimer = new System.Threading.Timer(new System.Threading.TimerCallback(PingTimerCallback), null, pingTimeSeconds * 1000, pingTimeSeconds * 1000); }
                if (pongTimeSeconds > 0) { pongTimer = new System.Threading.Timer(new System.Threading.TimerCallback(PongTimerCallback), null, pongTimeSeconds * 1000, pongTimeSeconds * 1000); }

                fragmentParsingState = 1;
                return len; // TODO: Technically we need to return the header length before UTF8 convert.
            }
            else if (state == ConnectionStates.Connected)
            {
                if (fragmentParsingState == 1)
                {
                    // Parse a websocket fragment header
                    if (len < 2) return 0;
                    int headsize = 2;
                    accopcodes = buffer[offset];
                    accmask = ((buffer[offset + 1] & 0x80) != 0);
                    acclen = (buffer[offset + 1] & 0x7F);

                    if ((accopcodes & 0x0F) == 8)
                    {
                        // Close the websocket
                        Debug("Websocket got closed fragment.");
                        return -1;
                    }

                    // For control commands with no playloads like ping and pong, handle this here.
                    if (acclen == 0) { ProcessWsBuffer(null, 0, 0, accopcodes); return headsize; }

                    if (acclen == 126)
                    {
                        if (len < 4) return 0;
                        headsize = 4;
                        acclen = (buffer[offset + 2] << 8) + (buffer[offset + 3]);
                    }
                    else if (acclen == 127)
                    {
                        if (len < 10) return 0;
                        headsize = 10;
                        acclen = (buffer[offset + 6] << 24) + (buffer[offset + 7] << 16) + (buffer[offset + 8] << 8) + (buffer[offset + 9]);
                        Debug("Websocket receive large fragment: " + acclen);
                    }
                    if (accmask == true)
                    {
                        // TODO: Do unmasking here.
                        headsize += 4;
                    }
                    //parent.Debug("#" + counter + ": Websocket frag header - FIN: " + ((accopcodes & 0x80) != 0) + ", OP: " + (accopcodes & 0x0F) + ", LEN: " + acclen + ", MASK: " + accmask);
                    fragmentParsingState = 2;
                    return headsize;
                }
                if (fragmentParsingState == 2)
                {
                    // Parse a websocket fragment data
                    if (len < acclen) return 0;
                    //Console.Write("WSREAD: " + acclen + "\r\n");
                    ProcessWsBuffer(buffer, offset, acclen, accopcodes);
                    fragmentParsingState = 1;
                    return acclen;
                }
            }
            return 0;
        }

        private void ProcessWsBuffer(byte[] data, int offset, int len, int op)
        {
            int orglen = len;
            MemoryStream mem = null;
            if (((op & 0x40) != 0) && (inflateMemory != null))
            {
                // This is a deflate compressed frame
                inflateMemory.SetLength(0);
                inflateMemory.Write(data, offset, len);
                inflateMemory.Write(inflateEnd, 0, 4);
                inflateMemory.Seek(0, SeekOrigin.Begin);
                MemoryStream memoryStream = new MemoryStream();
                inflate.CopyTo(memoryStream);
                data = memoryStream.GetBuffer();
                offset = 0;
                len = (int)memoryStream.Length;
            }

            switch (op & 0x0F)
            {
                case 0x01: // This is a text frame
                    {
                        Debug("Websocket got string data, len = " + len);
                        if (onStringData != null) { onStringData(this, UTF8Encoding.UTF8.GetString(data, offset, len), orglen); }
                        break;
                    }
                case 0x02: // This is a birnay frame
                    {
                        Debug("Websocket got binary data, len = " + len);
                        if (onBinaryData != null) { onBinaryData(this, data, offset, len, orglen); }
                        break;
                    }
                case 0x09: // Ping
                    {
                        SendPong(data, offset, len);
                        break;
                    }
                case 0x0A: // Pong
                    {
                        break;
                    }
            }
            if (mem != null) { mem.Dispose(); mem = null; }
        }

        private Dictionary<string, string> ParseHttpHeader(string header)
        {
            string[] lines = header.Replace("\r\n", "\r").Split('\r');
            if (lines.Length < 2) { return null; }
            string[] directive = lines[0].Split(' ');
            Dictionary<string, string> values = new Dictionary<string, string>();
            values["_Action"] = directive[0];
            values["_Path"] = directive[1];
            values["_Protocol"] = directive[2];
            for (int i = 1; i < lines.Length; i++)
            {
                var j = lines[i].IndexOf(":");
                values[lines[i].Substring(0, j).ToLower()] = lines[i].Substring(j + 1).Trim();
            }
            return values;
        }

        // Return a modified base64 SHA384 hash string of the certificate public key
        public static string GetMeshKeyHash(X509Certificate cert)
        {
            return ByteArrayToHexString(new SHA384Managed().ComputeHash(cert.GetPublicKey()));
        }

        // Return a modified base64 SHA384 hash string of the certificate
        public static string GetMeshCertHash(X509Certificate cert)
        {
            return ByteArrayToHexString(new SHA384Managed().ComputeHash(cert.GetRawCertData()));
        }

        public static string ByteArrayToHexString(byte[] Bytes)
        {
            StringBuilder Result = new StringBuilder(Bytes.Length * 2);
            string HexAlphabet = "0123456789ABCDEF";
            foreach (byte B in Bytes) { Result.Append(HexAlphabet[(int)(B >> 4)]); Result.Append(HexAlphabet[(int)(B & 0xF)]); }
            return Result.ToString();
        }

        private bool VerifyServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            if (tlsCertFingerprint == null) return true;
            if ((tlsCertFingerprint.Length == 32) && (certificate.GetCertHashString().Equals(tlsCertFingerprint))) { return true; }
            if (tlsCertFingerprint.Length == 96)
            {
                if (GetMeshCertHash(certificate).Equals(tlsCertFingerprint)) { return true; }
                if (GetMeshKeyHash(certificate).Equals(tlsCertFingerprint)) { return true; }
            }

            string hash1 = GetMeshCertHash(certificate);
            string hash2 = certificate.GetCertHashString();
            Debug("VerifyServerCertificate: tlsCertFingerprint = " + tlsCertFingerprint);
            Debug("VerifyServerCertificate: Hash1 = " + hash1);
            Debug("VerifyServerCertificate: Hash2 = " + hash2);
            return ((tlsCertFingerprint == GetMeshKeyHash(certificate)) || (tlsCertFingerprint == certificate.GetCertHashString()));
        }

        public int SendString(string data)
        {
            if (state != ConnectionStates.Connected) return 0;
            Debug("WebSocketClient-SEND-String: " + data);
            byte[] buf = UTF8Encoding.UTF8.GetBytes(data);
            return SendFragment(buf, 0, buf.Length, 129);
        }

        public int SendBinary(byte[] data)
        {
            Debug("WebSocketClient-SEND-Binary-Len:" + data.Length);
            return SendFragment(data, 0, data.Length, 130);
        }

        public int SendBinary(byte[] data, int offset, int len) {
            Debug("WebSocketClient-SEND-Binary-Len:" + len);
            return SendFragment(data, offset, len, 130);
        }

        public int SendPing(byte[] data, int offset, int len)
        {
            Debug("WebSocketClient-SEND-Ping");
            return SendFragment(null, 0, 0, 137);
        }

        public int SendPong(byte[] data, int offset, int len)
        {
            Debug("WebSocketClient-SEND-Pong");
            return SendFragment(null, 0, 0, 138);
        }

        // Fragment op code (129 = text, 130 = binary)
        public int SendFragment(byte[] data, int offset, int len, byte op)
        {
            if (state != ConnectionStates.Connected) return 0;
            byte[] buf;

            // If deflate is active, attempt to compress the data here.
            if ((deflateMemory != null) && (len > 32) && (AllowCompression))
            {
                deflateMemory.SetLength(0);
                deflateMemory.Write(inflateStart, 0, 10);
                DeflateStream deflate = new DeflateStream(deflateMemory, CompressionMode.Compress, true);
                deflate.Write(data, offset, len);
                deflate.Dispose();
                deflate = null;
                if (deflateMemory.Length < len)
                {
                    // Use the compressed data
                    int newlen = (int)deflateMemory.Length;
                    buf = deflateMemory.GetBuffer();
                    len = newlen - 10;
                    op |= 0x40; // Add compression op
                } else {
                    // Don't use the compress data
                    // Convert the string into a buffer with 4 byte of header space.
                    buf = new byte[10 + len];
                    Array.Copy(data, offset, buf, 10, len);
                }
            }
            else
            {
                // Convert the string into a buffer with 4 byte of header space.
                buf = new byte[10 + len];
                if (len > 0) { Array.Copy(data, offset, buf, 10, len); }
            }

            // Check that everything is ok
            if (len < 0) { Dispose(); return 0; }

            if (len < 126)
            {
                // Small fragment
                buf[8] = op;
                buf[9] = (byte)(len & 0x7F);
                SendData(buf, 8, len + 2);
            }
            else if (len < 65535)
            {
                // Medium fragment
                buf[6] = op;
                buf[7] = 126;
                buf[8] = (byte)((len >> 8) & 0xFF);
                buf[9] = (byte)(len & 0xFF);
                SendData(buf, 6, len + 4);
            }
            else
            {
                // Large fragment
                buf[0] = op;
                buf[1] = 127;
                buf[6] = (byte)((len >> 24) & 0xFF);
                buf[7] = (byte)((len >> 16) & 0xFF);
                buf[8] = (byte)((len >> 8) & 0xFF);
                buf[9] = (byte)(len & 0xFF);
                SendData(buf, 0, len + 10);
            }

            return len;
        }

        private void SendData(byte[] buf) { SendData(buf, 0, buf.Length); }

        private void SendData(byte[] buf, int off, int len)
        {
            if (pendingSendCall)
            {
                lock (pendingSendBuffer) { pendingSendBuffer.Write(buf, off, len); }
            }
            else
            {
                pendingSendCall = true;
                try { wsstream.BeginWrite(buf, off, len, new AsyncCallback(WriteWebSocketAsyncDone), null); } catch (Exception) { Dispose(); return; }
            }
        }

        public static string GetProxyForUrlUsingPac(string DestinationUrl, string PacUri)
        {
            IntPtr WinHttpSession = Win32Api.WinHttpOpen("User", Win32Api.WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, IntPtr.Zero, IntPtr.Zero, 0);

            Win32Api.WINHTTP_AUTOPROXY_OPTIONS ProxyOptions = new Win32Api.WINHTTP_AUTOPROXY_OPTIONS();
            Win32Api.WINHTTP_PROXY_INFO ProxyInfo = new Win32Api.WINHTTP_PROXY_INFO();

            ProxyOptions.dwFlags = Win32Api.WINHTTP_AUTOPROXY_CONFIG_URL;
            ProxyOptions.dwAutoDetectFlags = (Win32Api.WINHTTP_AUTO_DETECT_TYPE_DHCP | Win32Api.WINHTTP_AUTO_DETECT_TYPE_DNS_A);
            ProxyOptions.lpszAutoConfigUrl = PacUri;

            // Get Proxy 
            bool IsSuccess = Win32Api.WinHttpGetProxyForUrl(WinHttpSession, DestinationUrl, ref ProxyOptions, ref ProxyInfo);
            Win32Api.WinHttpCloseHandle(WinHttpSession);

            if (IsSuccess) {
                return ProxyInfo.lpszProxy;
            } else {
                Console.WriteLine("Error: {0}", Win32Api.GetLastError());
                return null;
            }
        }

        public void Pause()
        {
            readPaused = true;
        }

        public void Resume()
        {
            if (readPaused == false) return;
            readPaused = false;
            if (shouldRead == true)
            {
                shouldRead = false;
                try { wsstream.BeginRead(readBuffer, readBufferLen, readBuffer.Length - readBufferLen, new AsyncCallback(OnTlsDataSink), this); } catch (Exception) { }
            }
        }

    }

}
