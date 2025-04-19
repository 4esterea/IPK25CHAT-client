using System;
using System.Threading.Tasks;
using System.Net.Sockets;

namespace IPK25_CHAT
{
    class Program
    {
        private static bool _Logging;
        static async Task Main(string[] args)
        {
            try
            {
                var arguments = CommandLineParser.Parse(args);
                _Logging = arguments.Logging;
                IChatProtocol protocol;
                if (arguments.TransportProtocol == TransportProtocol.Tcp)
                {
                    LogDebug($"Connecting to {arguments.ServerAddress}:{arguments.ServerPort} using TCP...");
                    protocol = new TcpProtocol(
                        arguments.ServerAddress,
                        arguments.ServerPort,
                        _Logging);
                    ((TcpProtocol)protocol).Logging = arguments.Logging;
                    LogDebug("Connected Successfully!");
                }
                else
                {
                    LogDebug($"Connecting to {arguments.ServerAddress}:{arguments.ServerPort} using UDP...");
                    protocol = new UdpProtocol(
                        arguments.ServerAddress, 
                        arguments.ServerPort, 
                        _Logging, 
                        arguments.UdpTimeout, 
                        arguments.UdpRetransmissions);
                    ((UdpProtocol)protocol).Logging = arguments.Logging;
                    LogDebug("Connected Successfully!");
                }
                
                LogDebug("Logging enabled");
                

                var client = new ChatClient(protocol, arguments.Logging);
                await client.RunAsync();
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"Connection error: {ex.Message}");
                Console.Error.WriteLine("Please make sure the server is running and accessible.");
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal error: {ex}");
                Environment.Exit(1);
            }
        }
        private static void LogDebug(string message)
        {
            if (_Logging)
                Console.Error.WriteLine($"MAIN: {message}");
        }
    }
} 