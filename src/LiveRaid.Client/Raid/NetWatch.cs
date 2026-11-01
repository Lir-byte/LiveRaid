using System.Collections.Generic;
using System.Text;
using Comfort.Common;
using UnityEngine;
#if LIVERAID_ONLINE
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
#endif

namespace LiveRaid.Client
{
    /// <summary>
    /// Measures what a player of a Fika raid feels as "lag" so the next report can tell the cause (F12 -> 1 "Log network and frame time", every 10 s).
    /// Fika shows the other players the host's ping, and that ping is the host's round trip: a host frame that takes 600 ms delays every reply by
    /// 600 ms (LiteNetLib answers from the main thread), so a freezing host and a bad line look the same from outside. Here, on the host:
    ///  - frame time of the host: frames per second, the worst frame, frames over 100 / 250 ms;
    ///  - for each connected player: round trip, packets waiting in the reliable queues (a backlog = a lost packet holding the rest back), the packet
    ///    size the line carries (MTU), time since the last packet, and the loss the host counts.
    /// A line goes to the log (Info) when something is off - a round trip over 250 ms, a frame over 250 ms, a backlog - else to the "net" detail trace.
    /// In single player only the frame time is measured.
    /// </summary>
    public static class NetWatch
    {
        private const float Window = 10f;

        private static float _windowStart;
        private static int _frames;
        private static float _sum;
        private static float _worst;
        private static int _over100;
        private static int _over250;
        private static int _peerSamples;
        private static int _peerSamplesBad;
        private static int _worstRtt;
        private static int _worstQueue;
        private static long _lossPercent;

        private static int _totalFrames;
        private static float _totalWorst;
        private static int _totalOver100;
        private static int _totalOver250;
        private static float _totalSum;

#if LIVERAID_ONLINE
        private static readonly List<NetPeer> _peers = new List<NetPeer>();
#endif

        public static string Summary
        {
            get
            {
                if (_totalFrames == 0)
                {
                    return "frame time: not measured";
                }

                StringBuilder sb = new StringBuilder();
                sb.Append($"frame time of this machine: {_totalFrames / System.Math.Max(0.001f, _totalSum):0} fps on average, worst frame {_totalWorst * 1000f:0} ms, frames over 100 ms {_totalOver100}, over 250 ms {_totalOver250}");
                if (_peerSamples > 0)
                {
                    sb.Append($"; Fika players: worst round trip {_worstRtt} ms, {_peerSamplesBad} of {_peerSamples} samples over 250 ms or backed up, worst reliable backlog {_worstQueue}, loss counted by the host {_lossPercent} %");
                }

                return sb.ToString();
            }
        }

        public static void Clear()
        {
            _windowStart = 0f;
            _frames = _over100 = _over250 = 0;
            _sum = _worst = 0f;
            _peerSamples = _peerSamplesBad = _worstRtt = _worstQueue = 0;
            _lossPercent = 0;
            _totalFrames = _totalOver100 = _totalOver250 = 0;
            _totalWorst = _totalSum = 0f;
        }

        /// <summary>Once per frame.</summary>
        public static void Tick()
        {
            if (!Settings.LogNetwork.Value)
            {
                return;
            }

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f || dt > 60f)
            {
                return;
            }

            if (_windowStart == 0f)
            {
                _windowStart = Time.unscaledTime;
            }

            _frames++;
            _totalFrames++;
            _sum += dt;
            _totalSum += dt;
            if (dt > _worst)
            {
                _worst = dt;
            }

            if (dt > _totalWorst)
            {
                _totalWorst = dt;
            }

            if (dt > 0.1f)
            {
                _over100++;
                _totalOver100++;
            }

            if (dt > 0.25f)
            {
                _over250++;
                _totalOver250++;
            }

            if (Time.unscaledTime - _windowStart < Window)
            {
                return;
            }

            Flush();
        }

        private static void Flush()
        {
            float fps = _frames / System.Math.Max(0.001f, _sum);
            bool bad = _worst > 0.25f;
            StringBuilder sb = new StringBuilder();
            sb.Append($"LR Net: this machine {fps:0} fps, worst frame {_worst * 1000f:0} ms (over 100 ms: {_over100}, over 250 ms: {_over250})");
#if LIVERAID_ONLINE
            if (FikaBackendUtils.IsServer && Singleton<IFikaNetworkManager>.Instantiated && Singleton<IFikaNetworkManager>.Instance is FikaServer server && server.NetServer != null)
            {
                _peers.Clear();
                server.NetServer.GetConnectedPeers(_peers);
                _lossPercent = server.NetServer.Statistics.PacketLossPercent;
                sb.Append($"; Fika players {_peers.Count}, loss {_lossPercent} %");
                foreach (NetPeer peer in _peers)
                {
                    int rtt = peer.RoundTripTime;
                    int queue = peer.GetPacketsCountInReliableQueue(true) + peer.GetPacketsCountInReliableQueue(false);
                    bool peerBad = rtt > 250 || queue > 30 || peer.TimeSinceLastPacket > 2f;
                    _peerSamples++;
                    if (peerBad)
                    {
                        _peerSamplesBad++;
                        bad = true;
                    }

                    if (rtt > _worstRtt)
                    {
                        _worstRtt = rtt;
                    }

                    if (queue > _worstQueue)
                    {
                        _worstQueue = queue;
                    }

                    sb.Append($"; {peer.Address}:{peer.Port} round trip {rtt} ms, reliable backlog {queue}, line packet {peer.Mtu} bytes, last packet {peer.TimeSinceLastPacket:0.0} s ago");
                }
            }
#endif
            string line = sb.ToString();
            if (bad)
            {
                Log.Info(line);
            }
            else
            {
                Log.Detail("net", line);
            }

            _windowStart = Time.unscaledTime;
            _frames = _over100 = _over250 = 0;
            _sum = _worst = 0f;
        }
    }
}
