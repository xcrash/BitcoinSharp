﻿/*
 * Copyright 2011 Google Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BitCoinSharp.Discovery;
using BitCoinSharp.Store;
using log4net;

namespace BitCoinSharp
{
    /// <summary>
    /// Maintain a number of connections to peers.
    /// </summary>
    /// <remarks>
    /// PeerGroup tries to maintain a constant number of connections to a set of distinct peers.
    /// Each peer runs a network listener in its own thread. When a connection is lost, a new peer
    /// will be tried after a delay as long as the number of connections less than the maximum.
    /// 
    /// <p/>Connections are made to addresses from a provided list. When that list is exhausted,
    /// we start again from the head of the list.
    /// 
    /// <p/>The PeerGroup can broadcast a transaction to the currently connected set of peers. It can
    /// also handle download of the block chain from peers, restarting the process when peers die.
    /// 
    /// @author miron@google.com (Miron Cuperman a.k.a devrandom)
    /// </remarks>
    public class PeerGroup
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof (PeerGroup));

        public const int DefaultConnectionDelayMillis = 5 * 1000;

        // Addresses to try to connect to, excluding active peers
        private readonly BlockingCollection<PeerAddress> _inactives;
        // Connection initiation thread
        private Thread _connectThread;
        // True if the connection initiation thread should be running
        private bool _running;
        // Currently active peers
        private readonly BlockingCollection<Peer> _peers;
        // The peer we are currently downloading the chain from
        private Peer _downloadPeer;
        // Callback for events related to chain download
        private IPeerEventListener _downloadListener;
        // Peer discovery sources, will be polled occasionally if there aren't enough in-actives.
        private readonly BlockingCollection<IPeerDiscovery> _peerDiscoverers;

        private readonly NetworkParameters _networkParameters;
        private readonly IBlockStore _blockStore;
        private readonly BlockChain _blockChain;
        private readonly int _connectionDelayMillis;

        /// <summary>
        /// Creates a PeerGroup with the given parameters and a default 5 second connection timeout.
        /// </summary>
        public PeerGroup(IBlockStore blockStore, NetworkParameters networkParameters, BlockChain blockChain)
            : this(blockStore, networkParameters, blockChain, DefaultConnectionDelayMillis)
        {
        }

        /// <summary>
        /// Creates a PeerGroup with the given parameters. The connectionDelayMillis parameter controls how long the
        /// PeerGroup will wait between attempts to connect to nodes or read from any added peer discovery sources.
        /// </summary>
        public PeerGroup(IBlockStore blockStore, NetworkParameters networkParameters, BlockChain blockChain,
            int connectionDelayMillis)
        {
            _blockStore = blockStore;
            _networkParameters = networkParameters;
            _blockChain = blockChain;
            _connectionDelayMillis = connectionDelayMillis;

            _inactives = new BlockingCollection<PeerAddress>();
            _peers = new BlockingCollection<Peer>();
            _peerDiscoverers = new BlockingCollection<IPeerDiscovery>();
        }

        /// <summary>
        /// Called when a peer is connected.
        /// </summary>
        public event EventHandler<PeerConnectedEventArgs> PeerConnected;

        /// <summary>
        /// Called when a peer is disconnected.
        /// </summary>
        public event EventHandler<PeerDisconnectedEventArgs> PeerDisconnected;

        /// <summary>
        /// Depending on the environment, this should normally be between 1 and 10, default is 4.
        /// </summary>
        /// <summary>
        /// Add an address to the list of potential peers to connect to.
        /// </summary>
        public void AddAddress(PeerAddress peerAddress)
        {
            // TODO(miron) consider de-duplication
            _inactives.Add(peerAddress);
        }

        /// <summary>
        /// Add addresses from a discovery source to the list of potential peers to connect to.
        /// </summary>
        public void AddPeerDiscovery(IPeerDiscovery peerDiscovery)
        {
            _peerDiscoverers.Add(peerDiscovery);
        }

        /// <summary>
        /// Starts the background thread that makes connections.
        /// </summary>
        public void Start()
        {
            _connectThread = new Thread(Run) {Name = "Peer group thread"};
            _running = true;
            _connectThread.Start();
        }

        /// <summary>
        /// Stop this PeerGroup.
        /// </summary>
        /// <remarks>
        /// The peer group will be asynchronously shut down. After it is shut down
        /// all peers will be disconnected and no threads will be running.
        /// </remarks>
        public void Stop()
        {
            lock (this)
            {
                if (_running)
                {
                    _connectThread.Interrupt();
                }
            }
        }

        /// <summary>
        /// Broadcast a transaction to all connected peers.
        /// </summary>
        /// <returns>Whether we sent to at least one peer.</returns>
        public bool BroadcastTransaction(Transaction tx)
        {
            var success = false;
            lock (_peers)
            {
                foreach (var peer in _peers)
                {
                    try
                    {
                        peer.BroadcastTransaction(tx);
                        success = true;
                    }
                    catch (IOException e)
                    {
                        Log.Error("failed to broadcast to " + peer, e);
                    }
                }
            }
            return success;
        }

        /// <summary>
        /// Repeatedly get the next peer address from the inactive queue
        /// and try to connect.
        /// </summary>
        /// <remarks>
        /// We can be terminated with Thread.interrupt. When an interrupt is received,
        /// we will ask the executor to shutdown and ask each peer to disconnect. At that point
        /// no threads or network connections will be active.
        /// </remarks>
        public void Run()
        {
            try
            {
                while (_running)
                {
                    if (_inactives.Count == 0)
                    {
                        DiscoverPeers();
                    }
                    else
                    {
                        TryNextPeer();
                    }

                    // We started a new peer connection, delay before trying another one
                    Thread.Sleep(_connectionDelayMillis);
                }
            }
            catch (ThreadInterruptedException)
            {
                lock (this)
                {
                    _running = false;
                }
            }
            //TODO: 
            //_peerPool.ShutdownNow();
            lock (_peers)
            {
                foreach (var peer in _peers)
                {
                    peer.Disconnect();
                }
            }
        }

        private void DiscoverPeers()
        {
            foreach (var peerDiscovery in _peerDiscoverers)
            {
                IEnumerable<EndPoint> addresses;
                try
                {
                    addresses = peerDiscovery.GetPeers();
                }
                catch (PeerDiscoveryException e)
                {
                    // Will try again later.
                    Log.Error("Failed to discover peer addresses from discovery source", e);
                    return;
                }

                foreach (var address in addresses)
                {
                    _inactives.Add(new PeerAddress((IPEndPoint) address));
                }

                if (_inactives.Count > 0) break;
            }
        }

        /// <summary>
        /// Try connecting to a peer. If we exceed the number of connections, delay and try
        /// again.
        /// </summary>
        /// <exception cref="ThreadInterruptedException"/>
        private void TryNextPeer()
        {
            var address = _inactives.Take();
            while (true)
            {
                try
                {
                    var peer = new Peer(_networkParameters, address, _blockStore.GetChainHead().Height, _blockChain);
                    Task.Factory.StartNew(
                        () =>
                        {
                            try
                            {
                                Log.Info("Connecting to " + peer);
                                peer.Connect();
                                _peers.Add(peer);
                                HandleNewPeer(peer);
                                peer.Run();
                            }
                            catch (PeerException ex)
                            {
                                // Do not propagate PeerException - log and try next peer. Suppress stack traces for
                                // exceptions we expect as part of normal network behaviour.
                                var cause = ex.InnerException;
                                var exception = cause as SocketException;
                                if (exception != null)
                                {
                                    if (exception.SocketErrorCode == SocketError.TimedOut)
                                        Log.Info("Timeout talking to " + peer + ": " + cause.Message);
                                    else
                                        Log.Info("Could not connect to " + peer + ": " + cause.Message);
                                }
                                else if (cause is IOException)
                                {
                                    Log.Info("Error talking to " + peer + ": " + cause.Message);
                                }
                                else
                                {
                                    Log.Error("Unexpected exception whilst talking to " + peer, ex);
                                }
                            }
                            finally
                            {
                                // In all cases, disconnect and put the address back on the queue.
                                // We will retry this peer after all other peers have been tried.
                                peer.Disconnect();

                                _inactives.Add(address);
                                //TODO: Ensure this is the logic that we expect.
                                if (_peers.TryTake(out peer))
                                    HandlePeerDeath(peer);
                            }
                        });
                    break;
                }
                    //catch (RejectedExecutionException)
                    //{
                    //    // Reached maxConnections, try again after a delay

                    //    // TODO - consider being smarter about retry. No need to retry
                    //    // if we reached maxConnections or if peer queue is empty. Also consider
                    //    // exponential backoff on peers and adjusting the sleep time according to the
                    //    // lowest backoff value in queue.
                    //}
                catch (BlockStoreException e)
                {
                    // Fatal error
                    Log.Error("Block store corrupt?", e);
                    _running = false;
                    throw new Exception(e.Message, e);
                }

                // If we got here, we should retry this address because an error unrelated
                // to the peer has occurred.
                // TODO: Code is unreachable?
                Thread.Sleep(_connectionDelayMillis);
            }
        }

        /// <summary>
        /// Start downloading the block chain from the first available peer.
        /// </summary>
        /// <remarks>
        /// If no peers are currently connected, the download will be started
        /// once a peer starts. If the peer dies, the download will resume with another peer.
        /// </remarks>
        /// <param name="listener">A listener for chain download events, may not be null.</param>
        public void StartBlockChainDownload(IPeerEventListener listener)
        {
            lock (this)
            {
                _downloadListener = listener;
                // TODO be more nuanced about which peer to download from. We can also try
                // downloading from multiple peers and handle the case when a new peer comes along
                // with a longer chain after we thought we were done.
                lock (_peers)
                {
                    var firstPeer = _peers.FirstOrDefault();
                    if (firstPeer != null)
                        StartBlockChainDownloadFromPeer(firstPeer);
                }
            }
        }

        /// <summary>
        /// Download the block chain from peers.
        /// </summary>
        /// <remarks>
        /// This method wait until the download is complete. "Complete" is defined as downloading
        /// from at least one peer all the blocks that are in that peer's inventory.
        /// </remarks>
        public void DownloadBlockChain()
        {
            var downloadListener = new DownloadListener();
            StartBlockChainDownload(downloadListener);
            downloadListener.Await();
        }

        protected void HandleNewPeer(Peer peer)
        {
            lock (this)
            {
                if (_downloadListener != null && _downloadPeer == null)
                    StartBlockChainDownloadFromPeer(peer);
                if (PeerConnected != null)
                {
                    PeerConnected(this, new PeerConnectedEventArgs(_peers.Count));
                }
            }
        }

        protected void HandlePeerDeath(Peer peer)
        {
            lock (this)
            {
                if (peer == _downloadPeer)
                {
                    _downloadPeer = null;
                    lock (_peers)
                    {
                        var firstPeer = _peers.FirstOrDefault();
                        if (_downloadListener != null && firstPeer != null)
                        {
                            StartBlockChainDownloadFromPeer(firstPeer);
                        }
                    }
                }

                if (PeerDisconnected != null)
                {
                    PeerDisconnected(this, new PeerDisconnectedEventArgs(_peers.Count));
                }
            }
        }

        private void StartBlockChainDownloadFromPeer(Peer peer)
        {
            lock (this)
            {
                peer.BlocksDownloaded +=
                    (sender, blocksDownloadedEventArgs) =>
                        _downloadListener.OnBlocksDownloaded((Peer) sender, blocksDownloadedEventArgs.Block,
                            blocksDownloadedEventArgs.BlocksLeft);
                peer.ChainDownloadStarted +=
                    (sender, e) => _downloadListener.OnChainDownloadStarted((Peer) sender, e.BlocksLeft);
                try
                {
                    peer.StartBlockChainDownload();
                }
                catch (IOException e)
                {
                    Log.Error("failed to start block chain download from " + peer, e);
                    return;
                }
                _downloadPeer = peer;
            }
        }
    }

    /// <summary>
    /// Called when a peer is connected.
    /// </summary>
    public class PeerConnectedEventArgs : EventArgs
    {
        /// <summary>
        /// The total number of connected peers.
        /// </summary>
        public int PeerCount { get; private set; }

        public PeerConnectedEventArgs(int peerCount)
        {
            PeerCount = peerCount;
        }
    }

    /// <summary>
    /// Called when a peer is disconnected.
    /// </summary>
    public class PeerDisconnectedEventArgs : EventArgs
    {
        /// <summary>
        /// The total number of connected peers.
        /// </summary>
        public int PeerCount { get; private set; }

        public PeerDisconnectedEventArgs(int peerCount)
        {
            PeerCount = peerCount;
        }
    }
}