﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading;

namespace MnnSocket
{
    public partial class AsyncSocketListenerItem : IDisposable
    {
        /// <summary>
        /// Definition for recording connected client's state
        /// </summary>
        class ClientState
        {
            // Listen Socket's IPEndPoint
            public IPEndPoint localEP = null;
            // Accept result's IPEndPoint
            public IPEndPoint remoteEP = null;
            // Client Socket.
            public Socket workSocket = null;
            // Size of receive buffer.
            public const int BufferSize = 8192;
            // Receive buffer.
            public byte[] buffer = new byte[BufferSize];
            // Received data string.
            public StringBuilder sb = new StringBuilder();
        }
    }

    public partial class AsyncSocketListenerItem : IDisposable
    {
        // Listen IPEndPoint
        private IPEndPoint listenEP = null;
        // Listen Socket
        private Socket listenSocket = null;
        // Run the listen function
        private Thread thread = null;

        // List of ClientState
        private List<ClientState> clientStateTable = new List<ClientState>();

        // Events of listener and client
        public event EventHandler<ListenerEventArgs> ListenerStarted;
        public event EventHandler<ListenerEventArgs> ListenerStopped;
        public event EventHandler<ClientEventArgs> ClientConnect;
        public event EventHandler<ClientEventArgs> ClientDisconn;
        public event EventHandler<ClientEventArgs> ClientReadMsg;
        public event EventHandler<ClientEventArgs> ClientSendMsg;

        // Methods ================================================================
        public void Dispose()
        {
            this.Stop();
            this.CloseClient();
        }

        /// <summary>
        /// Start AsyncSocketListener
        /// </summary>
        /// <param name="localEPs"></param>
        /// <exception cref="System.ApplicationException"></exception>
        /// <exception cref="System.Net.Sockets.SocketException"></exception>
        /// <exception cref="System.ObjectDisposedException"></exception>
        public void Start(IPEndPoint ep)
        {
            // Verify IPEndPoints
            IPEndPoint[] globalEPs = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            foreach (IPEndPoint globalEP in globalEPs) {
                if (ep.Equals(globalEP))
                    throw new ApplicationException(ep.ToString() + " is in listening.");
            }

            // Initialize the listenEP field of ListenerState
            this.listenEP = ep;

            // Initialize Socket
            this.listenSocket = new Socket(AddressFamily.InterNetwork,
                SocketType.Stream, ProtocolType.Tcp);
            this.listenSocket.Bind(ep);
            this.listenSocket.Listen(100);

            // Create a thread for running listenSocket
            this.thread = new Thread(new ParameterizedThreadStart(ListeningThread));
            this.thread.IsBackground = true;
            this.thread.Start(this);
        }

        /// <summary>
        /// Stop AsyncSocketListener
        /// </summary>
        public void Stop()
        {
            // Stop listener & The ListeningThread will stop by its exception automaticlly
            this.listenSocket.Dispose();

            if (this.thread == null || this.thread.ThreadState == ThreadState.Stopped ||
                this.thread.ThreadState == ThreadState.Aborted)
                return;

            // Ensure to abort the ListeningThread
            this.thread.Abort();
            this.thread.Join();         //死锁
            //Thread.Sleep(200);          //不死锁，却影响响应时间
            this.thread = null;
        }

        private void ListeningThread(object args)
        {
            AsyncSocketListenerItem item = args as AsyncSocketListenerItem;

            try {
                /// ** Report ListenerStarted event
                if (ListenerStarted != null)
                    ListenerStarted(this, new ListenerEventArgs(item.listenEP));

                while (true) {
                    // Start an asynchronous socket to listen for connections.
                    // Get the socket that handles the client request.
                    Socket handler = item.listenSocket.Accept();

                    // Create the state object.
                    ClientState cltState = new ClientState();
                    cltState.localEP = (IPEndPoint)handler.LocalEndPoint;
                    cltState.remoteEP = (IPEndPoint)handler.RemoteEndPoint;
                    cltState.workSocket = handler;

                    /// @@ 第一次收到数据后，才会进入ReadCallback。
                    /// @@ 所以在没有收到数据前，线程被迫中止，将进入ReadCallback，并使EndReceive产生异常
                    /// @@ 由于ReadCallback在出现异常时，自动关闭client Socket连接，所有本线程Accept的连接都将断开并删除
                    /// @@ 暂时想不出解决办法
                    // Start receive message from client
                    handler.BeginReceive(cltState.buffer, 0, ClientState.BufferSize, 0,
                        new AsyncCallback(ReadCallback), cltState);

                    // Add handler to ClientState
                    lock (clientStateTable) {
                        clientStateTable.Add(cltState);
                    }

                    /// ** Report ClientConnect event
                    if (ClientConnect != null)
                        ClientConnect(this, new ClientEventArgs(cltState.localEP, cltState.remoteEP, null));
                }
            }
            catch (Exception ex) {
                item.listenSocket.Dispose();

                /// ** Report ListenerStopped event
                if (ListenerStopped != null)
                    ListenerStopped(this, new ListenerEventArgs(item.listenEP));

                Console.WriteLine(ex.ToString());
            }
        }

        private void ReadCallback(IAsyncResult ar)
        {
            // Retrieve the state object and the handler socket
            // from the asynchronous state object.
            ClientState cltState = (ClientState)ar.AsyncState;

            try {
                // Read data from the client socket. 
                int bytesRead = cltState.workSocket.EndReceive(ar);

                if (bytesRead > 0) {
                    // There  might be more data, so store the data received so far.
                    cltState.sb.Append(UTF8Encoding.Default.GetString(
                        cltState.buffer, 0, bytesRead));

                    /// ** Report ClientReadMsg event
                    if (ClientReadMsg != null)
                        ClientReadMsg(this, new ClientEventArgs(cltState.localEP, cltState.remoteEP, cltState.sb.ToString()));

                    // Then clear the StringBuilder
                    cltState.sb.Clear();

                    // Restart receive message from client
                    cltState.workSocket.BeginReceive(cltState.buffer, 0, ClientState.BufferSize, 0,
                        new AsyncCallback(ReadCallback), cltState);
                }
                else {
                    // Just for closing the client socket, so the ErrorCode is Shutdown
                    throw new SocketException((int)SocketError.Shutdown);
                }
            }
            //catch (SocketException ex) {
            //}
            catch (Exception ex) {
                // Close socket of client & remove it form clientState
                lock (clientStateTable) {
                    if (clientStateTable.Contains(cltState)) {
                        //cltState.workSocket.Shutdown(SocketShutdown.Both);    // 已经调用Dispose()将引起内存访问错误
                        cltState.workSocket.Dispose();
                        clientStateTable.Remove(cltState);
                    }
                }

                /// ** Report ClientDisconn event
                if (ClientDisconn != null)
                    ClientDisconn(this, new ClientEventArgs(cltState.localEP, cltState.remoteEP, null));

                Console.WriteLine(ex.ToString());
            }
        }

        /// <summary>
        /// Send data to all EP
        /// </summary>
        /// <param name="data"></param>
        public void Send(string data)
        {
            lock (clientStateTable) {
                foreach (ClientState cltState in clientStateTable) {
                    cltState.workSocket.BeginSend(UTF8Encoding.Default.GetBytes(data), 0,
                        UTF8Encoding.Default.GetBytes(data).Length, 0,
                        new AsyncCallback(SendCallback), cltState);

                    /// ** Report ClientSendMsg event
                    if (ClientSendMsg != null)
                        ClientSendMsg(this, new ClientEventArgs(cltState.localEP, cltState.remoteEP, data));
                }
            }
        }

        /// <summary>
        /// Send data to specified EP
        /// </summary>
        /// <param name="ep"></param>
        /// <param name="data"></param>
        public void Send(IPEndPoint ep, string data)
        {
            lock (clientStateTable) {
                foreach (ClientState cltState in clientStateTable) {
                    if (cltState.workSocket.RemoteEndPoint.Equals(ep)) {
                        cltState.workSocket.BeginSend(UTF8Encoding.Default.GetBytes(data), 0,
                           UTF8Encoding.Default.GetBytes(data).Length, 0,
                           new AsyncCallback(SendCallback), cltState);

                        /// ** Report ClientSendMsg event
                        if (ClientSendMsg != null)
                            ClientSendMsg(this, new ClientEventArgs(cltState.localEP, cltState.remoteEP, data));
                    }
                }
            }
        }

        private void SendCallback(IAsyncResult ar)
        {
            try {
                // Retrieve the state object and the handler socket
                // from the asynchronous state object.
                ClientState cltState = (ClientState)ar.AsyncState;

                // Complete sending the data to the remote device.
                int bytesSent = cltState.workSocket.EndSend(ar);
            }
            catch (Exception ex) {
                Console.WriteLine(ex.ToString());
            }
        }

        /// <summary>
        /// Close all socket of Client
        /// </summary>
        public void CloseClient()
        {
            lock (clientStateTable) {
                // Stop all client socket
                foreach (ClientState cltState in clientStateTable) {
                    //cltState.workSocket.Shutdown(SocketShutdown.Both);
                    cltState.workSocket.Dispose();
                }
            }
        }

        /// <summary>
        /// Close specified socket of Client
        /// </summary>
        /// <param name="remoteEP"></param>
        public void CloseClient(IPEndPoint ep)
        {
            lock (clientStateTable) {
                // Find target from clientState
                foreach (ClientState cltState in clientStateTable) {
                    // Close this state & The ReadCallback will remove its ClientState
                    if (cltState.remoteEP.Equals(ep)) {
                        //cltState.workSocket.Shutdown(SocketShutdown.Both);
                        cltState.workSocket.Dispose();

                        break;
                    }
                }
            }
        }

        /// <summary>
        /// If contain the client
        /// </summary>
        /// <param name="ep"></param>
        /// <returns></returns>
        public bool ContainClient(IPEndPoint ep)
        {
            foreach (var item in clientStateTable) {
                if (item.remoteEP.Equals(ep))
                    return true;
            }

            return false;
        }
    }
}