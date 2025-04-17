using System;

namespace IPK25_CHAT
{
    public class CommandLineArguments
    {
        public TransportProtocol TransportProtocol { get; set; }
        public string ServerAddress { get; set; }
        public ushort ServerPort { get; set; } = 4567;
        public ushort UdpTimeout { get; set; } = 250;
        public byte UdpRetransmissions { get; set; } = 3;
        public bool Logging { get; set; } = false;

        public void Validate()
        {
            if (string.IsNullOrEmpty(ServerAddress))
                throw new ArgumentException("Server address (-s) is required");
        }

        public static void PrintHelp()
        {
            Console.WriteLine("Usage: ./ipk25-chat -t <tcp|udp> -s <host> [-p <port>] [-d <timeout>] [-r <retransmissions>] [-v]");
            Console.WriteLine("Options:");
            Console.WriteLine("  -t <tcp|udp>     Transport protocol (required)");
            Console.WriteLine("  -s <host>        Server address (required)");
            Console.WriteLine("  -p <port>        Server port (default: 4567)");
            Console.WriteLine("  -d <timeout>     UDP confirmation timeout in ms (default: 250)");
            Console.WriteLine("  -r <retransmissions> Maximum number of UDP retransmissions (default: 3)");
            Console.WriteLine("  -l              Enable logging (output to stderr)");
            Console.WriteLine("  -h              Show this help message");
        }
    }
} 