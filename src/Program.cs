using System;
using System.Threading.Tasks;
using System.Net.Sockets;

namespace IPK25_CHAT
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                var arguments = CommandLineParser.Parse(args);
                IChatProtocol protocol;
                if (arguments.TransportProtocol == TransportProtocol.Tcp)
                {
                    Console.WriteLine($"Connecting to{arguments.ServerAddress}:{arguments.ServerPort} using TCP...");
                    protocol = new TcpProtocol(arguments.ServerAddress, arguments.ServerPort);
                    Console.WriteLine("Connected Successfully!");
                }
                else
                {
                    Console.WriteLine($"Connecting to{arguments.ServerAddress}:{arguments.ServerPort} using UDP...");
                    protocol = new UdpProtocol(arguments.ServerAddress, arguments.ServerPort);
                    Console.WriteLine("Connected Successfully!");
                }

                var client = new ChatClient(protocol);
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
    }
} 