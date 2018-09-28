using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

namespace LCM.LCM
{
	public class TCPService
	{
        private TcpListener serverSocket;

        private Thread acceptThread;
        private List<ClientThread> clients = new List<ClientThread>();

        private int bytesCount = 0;
		
        /// <summary>
        /// Constructor of the TCP service object
        /// </summary>
        /// <param name="port">TCP port number</param>
		public TCPService(int port)
		{
			TcpListener tempTCPListener;
            IPAddress localAddr = IPAddress.Parse("0.0.0.0");
            tempTCPListener = new TcpListener(localAddr, port);

			tempTCPListener.Start();
			serverSocket = tempTCPListener;
            //serverSocket.setReuseAddress(true);
            //serverSocket.setLoopbackMode(false); // true *disables* loopback
			
			acceptThread = new Thread(AcceptThreadRun);
            acceptThread.IsBackground = true;
			acceptThread.Start();
			
			long inittime = DateTime.Now.Ticks / 10000;
			long starttime = DateTime.Now.Ticks / 10000;

			while (true)
			{
				try
				{
					System.Threading.Thread.Sleep(1000);
				}
				catch (System.Threading.ThreadInterruptedException)
				{
				}

				long endtime = System.DateTime.Now.Ticks / 10000;
				double dt = (endtime - starttime) / 1000.0;
				starttime = endtime;

				Console.WriteLine("{0:yyyy-MM-dd HH:mm:ss.fff} : {1,10:N} kB/s, {2:D} clients", DateTime.Now, bytesCount / 1024.0 / dt, clients.Count);
				bytesCount = 0;
			}
		}
		
        /// <summary>
        /// Synchronously send a message to all clients
        /// </summary>
        /// <param name="channel">channel name</param>
        /// <param name="data">data to be relayed</param>
		public void Relay(byte[] channel, byte[] data)
        {
            string chanstr = System.Text.Encoding.GetEncoding("US-ASCII").GetString(channel);

			lock (clients)
			{
				foreach (ClientThread client in clients)
				{
					client.Send(chanstr, channel, data);
				}
			}
		}
		
		private void AcceptThreadRun()
		{
			while (true)
			{
				try
				{
					System.Net.Sockets.TcpClient clientSock = serverSocket.AcceptTcpClient();
					
					ClientThread client = new ClientThread(this, clientSock);
					client.Start();
					
					lock (clients)
					{
						clients.Add(client);
					}
				}
				catch (IOException)
				{
				}
			}
		}

        private class ClientThread
        {
            private TCPService service;
            private TcpClient sock;
            private BinaryReader ins;
            private BinaryWriter outs;
            private Thread thread;

            private Thread sendThread;
            private BlockingCollection<Message> sendQueue;

            const int maxQueueLenth = 15000;

            private class SubscriptionRecord
            {
                internal string regex;
                internal Regex pat;

                public SubscriptionRecord(string regex)
                {
                    this.regex = regex;
                    this.pat = new Regex(regex);
                }

                public override bool Equals(object obj)
                {
                    SubscriptionRecord rec = obj as SubscriptionRecord;
                    if (rec == null) return false;

                    return rec.regex == regex;
                }

                public override int GetHashCode()
                {
                    return regex.GetHashCode();
                }
            }

            List<SubscriptionRecord> subscriptions = new List<SubscriptionRecord>();

            public ClientThread(TCPService service, TcpClient sock)
            {
                this.service = service;
                this.sock = sock;

                ins = new BinaryReader(sock.GetStream());
                outs = new BinaryWriter(sock.GetStream());

                outs.Write(TCPProvider.MAGIC_SERVER);
                outs.Write(TCPProvider.VERSION);
            }

            public void Start()
            {
                if (sendThread == null)
                {
                    sendQueue = new BlockingCollection<Message>();
                    sendThread = new Thread(SendWorker);
                    sendThread.IsBackground = true;
                    sendThread.Start();
                }

                if (thread == null)
                {
                    thread = new Thread(Run);
                    thread.IsBackground = true;
                    thread.Start();
                }
            }

            public void Run()
            {
                // read messages until something bad happens.
                try
                {
                    while (true)
                    {
                        int type = ins.ReadInt32();
                        int channellen = ins.ReadInt32();
                        if (channellen > 1000)
                        {
                            Console.WriteLine("Message's channel size over 1000 bytes closing client");
                            Console.WriteLine(((IPEndPoint)sock.Client.RemoteEndPoint).Address.ToString());
                            break;
                        }
                        if (type == TCPProvider.MESSAGE_TYPE_PUBLISH)
                        {
                            byte[] channel = new byte[channellen];
                            ReadInput(ins.BaseStream, channel, 0, channel.Length);

                            int datalen = ins.ReadInt32();
                            byte[] data = new byte[datalen];
                            ReadInput(ins.BaseStream, data, 0, data.Length);

                            service.Relay(channel, data);

                            service.bytesCount += channellen + datalen + 8;
                        }
                        else if (type == TCPProvider.MESSAGE_TYPE_SUBSCRIBE)
                        {
                            byte[] channel = new byte[channellen];
                            ReadInput(ins.BaseStream, channel, 0, channel.Length);

                            lock (subscriptions)
                            {
                                string s_channel = System.Text.Encoding.GetEncoding("US-ASCII").GetString(channel);
                                SubscriptionRecord s = 
                                    new SubscriptionRecord(
                                        s_channel
                                    );
                                Console.WriteLine("Subscribe: {0}", s_channel);
                                if (!subscriptions.Contains(s))
                                {
                                    Console.WriteLine("Adding subscription");
                                    subscriptions.Add(s);
                                }
                                else
                                {
                                    Console.WriteLine("Already subscribed");
                                }
                                    
                            }
                        }
                        else if (type == TCPProvider.MESSAGE_TYPE_UNSUBSCRIBE)
                        {
                            byte[] channel = new byte[channellen];
                            ReadInput(ins.BaseStream, channel, 0, channel.Length);

                            string re = System.Text.Encoding.GetEncoding("US-ASCII").GetString(channel);
                            lock (subscriptions)
                            {
                                for (int i = 0, n = subscriptions.Count; i < n; i++)
                                {
                                    if (subscriptions[i].pat.IsMatch(re))
                                    {
                                        subscriptions.RemoveAt(i);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (SendQueueFullException)
                {
                    Console.WriteLine(((IPEndPoint)sock.Client.RemoteEndPoint).Address.ToString());
                    Console.WriteLine("Send queue was full, closing socket - subscriptions were: ");
                    Console.WriteLine(string.Join(", ", subscriptions.Select(s => s.regex)));
                }
                catch (OverflowException e)
                {
                    Console.WriteLine(e.ToString());
                    Console.WriteLine(((IPEndPoint)sock.Client.RemoteEndPoint).Address.ToString());
                }


                // Something bad happened, close this connection.
                try
                {
                    sock.Close();
                }
                catch (IOException)
                {
                }

                lock (service.clients)
                {
                    service.clients.Remove(this);
                }
                sendQueue.CompleteAdding();
            }

            public void SendWorker()
            {
                while (!sendQueue.IsAddingCompleted)
                {
                    Message msg;
                    if (sendQueue.TryTake(out msg, 10))
                    {
                        try
                        {
                            lock (subscriptions)
                            {
                                foreach (SubscriptionRecord sr in subscriptions)
                                {
                                    if (sr.pat.IsMatch(msg.ChannelString))
                                    {
                                        outs.Write(TCPProvider.MESSAGE_TYPE_PUBLISH);
                                        outs.Write(msg.Channel.Length);
                                        outs.Write(msg.Channel);
                                        outs.Write(msg.Data.Length);
                                        outs.Write(msg.Data);
                                        outs.Flush();

                                        break; 
                                    }
                                }
                            }
                        }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                        catch (InvalidOperationException) { }
                        catch (NotSupportedException) { }
                    }
                }
            }

            public virtual void Send(string chanstr, byte[] channel, byte[] data)
            {
                if (sendQueue.Count > maxQueueLenth) throw new SendQueueFullException();

                try
                {
                    sendQueue.Add(new Message
                    {
                        ChannelString = chanstr,
                        Channel = channel,
                        Data = data
                    });
                }
                catch (InvalidOperationException) { return; }
            }

            /******************************* Helper methods *******************************/

	        /// <summary>Reads a number of characters from the current source Stream and writes the data to the target array at the specified index.</summary>
	        /// <param name="sourceStream">The source Stream to read from.</param>
	        /// <param name="target">Contains the array of characteres read from the source Stream.</param>
	        /// <param name="start">The starting index of the target array.</param>
	        /// <param name="count">The maximum number of characters to read from the source Stream.</param>
	        /// <returns>The number of characters read. The number will be less than or equal to count depending on the data available in the source Stream. Returns -1 if the end of the stream is reached.</returns>
	        private static int ReadInput(Stream sourceStream, byte[] target, int start, int count)
	        {
		        // Returns 0 bytes if not enough space in target
                if (target.Length == 0)
                {
                    return 0;
                }

		        byte[] receiver = new byte[target.Length];
		        int bytesRead = sourceStream.Read(receiver, start, count);

		        // Returns -1 if EOF
                if (bytesRead == 0)
                {
                    return -1;
                }

                for (int i = start; i < start + bytesRead; i++)
                {
                    target[i] = (byte) receiver[i];
                }
                        
		        return bytesRead;
	        }

            private class Message
            {
                public string ChannelString { get; set; }
                public byte[] Channel { get; set; }
                public byte[] Data { get; set; }
            }
        }
	}

    class SendQueueFullException : Exception { }
}