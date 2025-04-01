using System;

namespace IPK25_CHAT
{
    public class CommandLineParser
    {
        public static CommandLineArguments Parse(string[] args)
        {
            var arguments = new CommandLineArguments();
            
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-t":
                        if (i + 1 >= args.Length) throw new ArgumentException("Missing value for -t argument");
                        arguments.TransportProtocol = args[++i];
                        break;
                        
                    case "-s":
                        if (i + 1 >= args.Length) throw new ArgumentException("Missing value for -s argument");
                        arguments.ServerAddress = args[++i];
                        break;
                        
                    case "-p":
                        if (i + 1 >= args.Length) throw new ArgumentException("Missing value for -p argument");
                        if (!ushort.TryParse(args[++i], out ushort port))
                            throw new ArgumentException("Invalid port number");
                        arguments.ServerPort = port;
                        break;
                        
                    case "-d":
                        if (i + 1 >= args.Length) throw new ArgumentException("Missing value for -d argument");
                        if (!ushort.TryParse(args[++i], out ushort timeout))
                            throw new ArgumentException("Invalid timeout value");
                        arguments.UdpTimeout = timeout;
                        break;
                        
                    case "-r":
                        if (i + 1 >= args.Length) throw new ArgumentException("Missing value for -r argument");
                        if (!byte.TryParse(args[++i], out byte retransmissions))
                            throw new ArgumentException("Invalid retransmissions value");
                        arguments.UdpRetransmissions = retransmissions;
                        break;
                        
                    case "-h":
                        CommandLineArguments.PrintHelp();
                        Environment.Exit(0);
                        break;
                }
            }

            arguments.Validate();
            return arguments;
        }
    }
} 