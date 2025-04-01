using System;

namespace IPK25_CHAT
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                var arguments = CommandLineParser.Parse(args);
                Console.WriteLine($"Transport Protocol: {arguments.TransportProtocol}");
                Console.WriteLine($"Server Address: {arguments.ServerAddress}");
                Console.WriteLine($"Server Port: {arguments.ServerPort}");
                Console.WriteLine($"UDP Timeout: {arguments.UdpTimeout}ms");
                Console.WriteLine($"UDP Retransmissions: {arguments.UdpRetransmissions}");
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Console.WriteLine("Use -h for help");
                Environment.Exit(1);
            }
        }
    }
}