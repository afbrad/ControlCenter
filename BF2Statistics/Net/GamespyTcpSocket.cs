﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using BF2Statistics.Database;

namespace BF2Statistics.Net
{
    /// <summary>
    /// This class represents a high performance Async Tcp Socket wrapper
    /// that is used to act as a base for all Gamespy protocol Tcp
    /// connections.
    /// </summary>
    public abstract class GamespyTcpSocket : IDisposable
    {
        /// <summary>
        /// Max number of concurrent open and active connections. Increasing this number
        /// will also increase the IO Buffer by <paramref name="BufferSizePerEventArg"/> * 2
        /// </summary>
        protected readonly int MaxNumConnections;

        /// <summary>
        /// The initial size of the concurrent accept pool when accepting new connections. 
        /// High volume of connections will increase the pool size if need be
        /// <remarks>4 should be a pretty good init compacity since accepting a client is pretty fast</remarks>
        /// </summary>
        protected readonly int ConcurrentAcceptPoolSize = 4;

        /// <summary>
        /// The amount of bytes each SocketAysncEventArgs object
        /// will get assigned to in the buffer manager.
        /// </summary>
        protected readonly int BufferSizePerEventArg = 256;

        /// <summary>
        /// Our Listening Socket
        /// </summary>
        protected Socket Listener;

        /// <summary>
        /// Buffers for sockets are unmanaged by .NET, which means that
        /// memory will get fragmented because the GC can't touch these
        /// byte arrays. So we will manage our buffers manually
        /// </summary>
        protected BufferManager BufferManager;

        /// <summary>
        /// Use a Semaphore to prevent more then the MaxNumConnections
        /// clients from logging in at once.
        /// </summary>
        protected SemaphoreSlim MaxConnectionsEnforcer;

        /// <summary>
        /// A pool of reusable SocketAsyncEventArgs objects for accept operations
        /// </summary>
        protected SocketAsyncEventArgsPool SocketAcceptPool;

        /// <summary>
        /// A pool of reusable SocketAsyncEventArgs objects for receive and send socket operations
        /// </summary>
        protected SocketAsyncEventArgsPool SocketReadWritePool;

        /// <summary>
        /// Indicates whether the socket is listening for connections
        /// </summary>
        public bool IsListening { get; protected set; }

        /// <summary>
        /// If set to True, new connections will be immediatly closed and ignored.
        /// </summary>
        public bool IgnoreNewConnections { get; protected set; }

        /// <summary>
        /// Indicates whether this object has been disposed yet
        /// </summary>
        public bool IsDisposed { get; protected set; }

        public GamespyTcpSocket(int Port, int MaxConnections)
        {
            // Create our Socket
            Listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            // Set Socket options
            Listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);
            Listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, false);
            Listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            // Bind to our port
            Listener.Bind(new IPEndPoint(IPAddress.Any, Port));
            Listener.Listen(25);

            // Set the rest of our internals
            MaxNumConnections = MaxConnections;
            MaxConnectionsEnforcer = new SemaphoreSlim(MaxNumConnections, MaxNumConnections);
            SocketAcceptPool = new SocketAsyncEventArgsPool(ConcurrentAcceptPoolSize);
            SocketReadWritePool = new SocketAsyncEventArgsPool(MaxNumConnections);

            // Create our Buffer Manager for IO operations. 
            // Always allocate double space, one for recieving, and another for sending
            BufferManager = new BufferManager(
                BufferSizePerEventArg * MaxNumConnections * 2,
                BufferSizePerEventArg
            ); 

            // Assign our Connection Accept SocketAsyncEventArgs object instances
            for (int i = 0; i < ConcurrentAcceptPoolSize; i++)
            {
                SocketAsyncEventArgs SockArg = new SocketAsyncEventArgs();
                SockArg.Completed += (s, e) => PrepareAccept(e);

                // Do NOT assign buffer space for Accept operations!               
                // AcceptAsync does not take require a parameter for buffer size.
                SocketAcceptPool.Push(SockArg);
            }

            // Assign our Connection IO SocketAsyncEventArgs object instances
            for (int i = 0; i < MaxNumConnections * 2; i++)
            {
                SocketAsyncEventArgs SockArg = new SocketAsyncEventArgs();
                BufferManager.AssignBuffer(SockArg);
                SocketReadWritePool.Push(SockArg);
            }

            // set public internals
            IsListening = true;
            IgnoreNewConnections = false;
            IsDisposed = false;
        }

        ~GamespyTcpSocket()
        {
            if (!IsDisposed)
                Dispose();
        }

        /// <summary>
        /// Releases all Objects held by this socket. Will also
        /// shutdown the socket if its still running
        /// </summary>
        public void Dispose()
        {
            // no need to do this again
            if (IsDisposed) return;
            IsDisposed = true;

            // Shutdown sockets
            if (IsListening)
                ShutdownSocket();

            // Dispose all AcceptPool AysncEventArg objects
            while (SocketAcceptPool.Count > 0)
                SocketAcceptPool.Pop().Dispose();

            // Dispose all ReadWritePool AysncEventArg objects
            while (SocketReadWritePool.Count > 0)
                SocketReadWritePool.Pop().Dispose();

            // Dispose the buffer manager after disposing all EventArgs
            BufferManager.Dispose();
            MaxConnectionsEnforcer.Dispose();
            Listener.Dispose();
        }

        /// <summary>
        /// When called, this method will stop accepting new clients, and stop all 
        /// I/O ops on all connections. Dispose still needs to be called afterwards!
        /// </summary>
        protected void ShutdownSocket()
        {
            // Only do this once
            if (!IsListening) return;
            IsListening = false;

            // Stop accepting connections
            try {
                Listener.Shutdown(SocketShutdown.Both);
            }
            catch { }
            
            // Close the listener
            Listener.Close();
        }

        /// <summary>
        /// Releases the Stream's SocketAsyncEventArgs back to the pool,
        /// and free's up another slot for a new client to connect
        /// </summary>
        /// <param name="Stream"></param>
        protected void Release(GamespyTcpStream Stream)
        {
            // Make sure the connection is closed properly
            if(!Stream.SocketClosed)
                Stream.Close();

            // If we are still registered for this event, then the EventArgs should
            // NEVER be disposed here, or we have an error to fix
            if (Stream.DisposedEventArgs)
                throw new Exception("Event Args were disposed imporperly!");

            // Get our ReadWrite AsyncEvent object back
            SocketReadWritePool.Push(Stream.ReadEventArgs);
            SocketReadWritePool.Push(Stream.WriteEventArgs);

            // Now that we have another set of AsyncEventArgs, we can
            // release this users Semephore lock, allowing another connection
            MaxConnectionsEnforcer.Release();
        }

        /// <summary>
        /// Begins accepting a new Connection asynchronously
        /// </summary>
        protected async void StartAcceptAsync()
        {
            // Fetch ourselves an available AcceptEventArg for the next connection
            SocketAsyncEventArgs AcceptEventArg;
            if(SocketAcceptPool.Count > 0)
            {
                try
                {
                    AcceptEventArg = SocketAcceptPool.Pop();
                }
                catch
                {
                    AcceptEventArg = new SocketAsyncEventArgs();
                    AcceptEventArg.Completed += (s, e) => PrepareAccept(e);
                }
            }
            else
            {
                // NO SOCKS AVAIL!
                AcceptEventArg = new SocketAsyncEventArgs();
                AcceptEventArg.Completed += (s, e) => PrepareAccept(e);
            }

            try
            {
                // Enforce max connections. If we are capped on connections, the new connection will stop here,
                // and retrun once a connection is opened up from the Release() method
                await MaxConnectionsEnforcer.WaitAsync();

                // Begin accpetion connections
                bool willRaiseEvent = Listener.AcceptAsync(AcceptEventArg);

                // If we wont raise event, that means a connection has already been accepted syncronously
                // and the Accept_Completed event will NOT be fired. So we manually call ProcessAccept
                if (!willRaiseEvent)
                    PrepareAccept(AcceptEventArg);
            }
            catch(ObjectDisposedException)
            {
                // Happens when the server is shutdown
            }
            catch(Exception e)
            {
                Program.ErrorLog.Write(
                    "ERROR: [GamespySocket.StartAccept] An Exception was thrown while attempting to recieve"
                    + " a conenction. Generating Exception Log"
                );
                ExceptionHandler.GenerateExceptionLog(e);
            }
        }

        /// <summary>
        /// Once a connection has been received, its handed off here to convert it into
        /// our client object, and prepared to be handed off to the parent for processing
        /// </summary>
        /// <param name="AcceptEventArg"></param>
        private void PrepareAccept(SocketAsyncEventArgs AcceptEventArg)
        {
            // If we do not get a success code here, we have a bad socket
            if(IgnoreNewConnections || AcceptEventArg.SocketError != SocketError.Success)
            {
                // This method closes the socket and releases all resources, both
                // managed and unmanaged. It internally calls Dispose.           
                AcceptEventArg.AcceptSocket.Close();

                // Put the SAEA back in the pool.
                SocketAcceptPool.Push(AcceptEventArg);
                StartAcceptAsync();
                return;
            }

            // Begin accepting a new connection
            StartAcceptAsync();

            // Grab a send/recieve object
            SocketAsyncEventArgs ReadArgs = SocketReadWritePool.Pop();
            SocketAsyncEventArgs WriteArgs = SocketReadWritePool.Pop();

            // Pass over the reference to the new socket that is handling
            // this specific stream, and dereference it so we can hand the
            // acception event back over
            ReadArgs.AcceptSocket = AcceptEventArg.AcceptSocket;
            WriteArgs.AcceptSocket = AcceptEventArg.AcceptSocket;
            AcceptEventArg.AcceptSocket = null;

            // Hand back the AcceptEventArg so another connection can be accepted
            SocketAcceptPool.Push(AcceptEventArg);

            // Hand off processing to the parent
            GamespyTcpStream Stream = null;
            try
            {
                Stream = new GamespyTcpStream(ReadArgs, WriteArgs);
                ProcessAccept(Stream);
            }
            catch (Exception e)
            {
                // Report Error
                Program.ErrorLog.Write("ERROR: An Error occured at [GamespyTcpSocket.PrepareAccept] : Generating Exception Log");
                ExceptionHandler.GenerateExceptionLog(e);

                // Make sure the connection is closed properly
                if (Stream != null && !Stream.SocketClosed)
                    Stream.Close();

                // Get our ReadWrite AsyncEvent object back
                ReadArgs.AcceptSocket.Close();
                SocketReadWritePool.Push(ReadArgs);
                SocketReadWritePool.Push(WriteArgs);

                // Now that we have another set of AsyncEventArgs, we can
                // release this users Semephore lock, allowing another connection
                MaxConnectionsEnforcer.Release();
            }
        }

        /// <summary>
        /// When a new connection is established, the parent class is responsible for
        /// processing the connected client
        /// </summary>
        /// <param name="Stream">A GamespyTcpStream object that wraps the I/O AsyncEventArgs and socket</param>
        protected abstract void ProcessAccept(GamespyTcpStream Stream);
    }
}
