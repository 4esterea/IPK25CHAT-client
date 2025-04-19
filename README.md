# IPK25 Chat Client

## Overview
IPK25 Chat Client is a network chat application that allows users to communicate using TCP or UDP protocols. The client supports authentication, channel joining, message exchange, and graceful disconnection. It is built with .NET and targets network reliability on both local and remote hosts.

## Executive Summary: Chat Client Communication Theory

### Fundamental Concepts
The chat client operates as a network client that connects to a server using either TCP or UDP. Through a series of command exchanges, the client authenticates, joins channels, sends chat messages, and disconnects safely.

### TCP Communication
- Establishes a reliable connection using the TCP protocol.
- Uses a connection attempt loop, with timeouts and debug logging for connection reliability.
- Supports authentication, channel join, message sending, and BYE messages.
- Performs message parsing and handles different types of server replies.

### UDP Communication
- Uses a connectionless approach with built-in confirmation and retry mechanisms.
- Every message is tagged with a unique message identifier.
- After sending a message, the client awaits a confirmation from the server.
- Implements retransmission strategies and handles various UDP message types such as CONFIRM, REPLY, ERR, and BYE.

## Interesting Source Code Sections

### Protocol Implementation Details

Inheritance is used to provide a common interface and shared functionality for both TCP and UDP protocols. An abstract base class (e.g., ChatProtocolBase) defines the common methods and events. The TCP and UDP classes then inherit and override these methods to implement protocol-specific behaviors.

Example of a TCP protocol class inheriting from the base:
```csharp
public class TcpProtocol : ChatProtocolBase
{
    public override async Task SendAuthAsync(string username, string displayName, string secret)
    {
        // TCP-specific implementation of authentication
        await SendCommandAsync($"AUTH {username} AS {displayName} USING {secret}");
    }

    // Other overrides...
}
```

Example of a UDP protocol class inheriting from the base:
```csharp
public class UdpProtocol : ChatProtocolBase
{
    public override async Task SendAuthAsync(string username, string displayName, string secret)
    {
        // Create binary AUTH message and send over UDP with confirmation
        byte[] data = CreateBinaryMessage(0x02, username, displayName, secret);
        ushort msgId = BitConverter.ToUInt16(data, 1);
        await SendWithConfirmationAsync(data, msgId);
    }

    // Other overrides...
}
```

### Event-based Message Delivery

The common event is declared and raised in a base class so both protocol implementations can use it to deliver messages.

```csharp
public event EventHandler<MessageReceivedEventArgs> MessageReceived;

protected void OnMessageReceived(MessageType type, string content, string displayName)
{
    MessageReceived?.Invoke(this, new MessageReceivedEventArgs(type, content, displayName));
}
```

The ChatClient subscribes to the MessageReceived event from the protocol instance. This event is fired whenever a message is processed and forwarded by the protocol (TCP/UDP). The handling method in ChatClient receives the event arguments and then proceeds to process the content depending on the message type.

```csharp
public ChatClient(IChatProtocol protocol, bool verboseLogging = false)
        {
            _protocol = protocol;
            _protocol.MessageReceived += HandleMessageReceived;
            // Other initialization...
        }
        
private void HandleMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            try
            {
                switch (e.Type)
                {
                    case MessageType.Reply:
                        // Reply message handling logic
                    case MessageType.Message:
                        // Error message handling logic
                        //...

```

## Usage

To run the chat client, execute the following command:

```csharp
./ipk25chat-client -t <tcp|udp> -s <host> [-p <port>] [-d <timeout>] [-r <retransmissions>] [-l] [-h]
```

Options:
-t <tcp|udp>           Specifies the transport protocol (required). Choose either 'tcp' or 'udp'.
-s <host>              Specifies the server address (required).
-p <port>              Specifies the server port (default: 4567).
-d <timeout>           Sets the UDP confirmation timeout in milliseconds (default: 250).
-r <retransmissions>   Sets the maximum number of UDP retransmissions (default: 3).
-l                     Enables logging (outputs to stderr).
-h                     Displays help message.

## Testing

### Test Case 1: Chat Client launch using -h option

**What was tested:**
The functionality of the -h option for the Chat Client.

**Why it was tested:**
To ensure the application displays the help message when the help flag is used.

**Command:**
```bash
./ipk25chat-client -h
```

**Output:**
```bash
Usage: ./ipk25chat-client -t <tcp|udp> -s <host> [-p <port>] [-d <timeout>] [-r <retransmissions>] [-l] [-h]
Options:
  -t <tcp|udp>     Transport protocol (required)
  -s <host>        Server address (required)
  -p <port>        Server port (default: 4567)
  -d <timeout>     UDP confirmation timeout in ms (default: 250)
  -r <retransmissions> Maximum number of UDP retransmissions (default: 3)
  -l              Enable logging (output to stderr)
  -h              Show this help message
```

**Expected output:**
```bash
Usage: ./ipk25chat-client -t <tcp|udp> -s <host> [-p <port>] [-d <timeout>] [-r <retransmissions>] [-l] [-h]
Options:
  -t <tcp|udp>     Transport protocol (required)
  -s <host>        Server address (required)
  -p <port>        Server port (default: 4567)
  -d <timeout>     UDP confirmation timeout in ms (default: 250)
  -r <retransmissions> Maximum number of UDP retransmissions (default: 3)
  -l              Enable logging (output to stderr)
  -h              Show this help message
```

### Test Case 2: Normal launch of the Chat Client using TCP

**What was tested:**
Normal launch of the chat client using the TCP protocol.

**Why it was tested:**
To verify that the client successfully establishes a TCP connection, initializes streams, and properly handles the authentication process.

**Command:**
```bash
 ./ipk25chat-client -t tcp -s anton5.fit.vutbr.cz 
```

**Output:**
```bash
/auth xzhdan00 <secret> DisplayName
Action Success: Authentication successful.
Server: DisplayName has joined `discord.general` via TCP.
```

**Expected output:**
```bash
/auth xzhdan00 <secret> DisplayName
Action Success: Authentication successful.
Server: DisplayName has joined `discord.general` via TCP.
```

### Test Case 3: Normal launch of the Chat Client using UDP

**What was tested:**

The normal launch of the chat client using the UDP protocol.

**Why it was tested:**

To verify that the client successfully establishes a UDP connection, initializes streams, and properly handles the authentication process.

**Command:**

```bash
./ipk25chat-client -t udp -s anton5.fit.vutbr.cz
```

**Output:**

```bash
/auth xzhdan00 <secret> DisplayName
Action Success: Authentication successful.
Server: DisplayName has joined `discord.general` via UDP.
```

**Expected output:**

```bash
/auth xzhdan00 <secret> DisplayName
Action Success: Authentication successful.
Server: DisplayName has joined `discord.general` via UDP.
```

### Test Case 4: Joining a channel using TCP

**What was tested:**

The ability to join a channel using the TCP protocol.

**Why it was tested:**

To verify that the client can successfully join a channel and receive confirmation from the server.

**Command:**

```bash
./ipk25chat-client -t tcp -s anton5.fit.vutbr.cz
```

**Output:**

```bash
/auth xzhdan00 <secret> DisplayName
Action Success: Authentication successful.
Server: DisplayName has joined `discord.general` via TCP.
/join discord.test
Action Success: Channel discord.test successfully joined.
Server: DisplayName has switched from `discord.general` to `discord.test`.
Server: DisplayName has joined `discord.test` via TCP.
```

**Expected output:**

```bash
/auth xzhdan00 <secret> DisplayName
Action Success: Authentication successful.
Server: DisplayName has joined `discord.general` via TCP.
/join discord.test
Action Success: Channel discord.test successfully joined.
Server: DisplayName has switched from `discord.general` to `discord.test`.
Server: DisplayName has joined `discord.test` via TCP.
```

### Test Case 5: Joining a channel using UDP

**What was tested:**
The ability to join a channel using the UDP protocol.

**Why it was tested:**
To verify that the client can successfully join a channel and receive confirmation from the server.

**Command:**
```bash
./ipk25chat-client -t udp -s anton5.fit.vutbr.cz
```
**Output:**
```bash
/auth xzhdan00 <secret> DisplayName
Action Success: Authentication successful.
Server: DisplayName has joined `discord.general` via UDP.
/join discord.test
Action Success: Channel discord.test successfully joined.
Server: DisplayName has switched from `discord.general` to `discord.test`.
Server: DisplayName has joined `discord.test` via UDP.
```

**Expected output:**
```bash
/auth xzhdan00 <secret> DisplayName
Action Success: Authentication successful.
Server: DisplayName has joined `discord.general` via UDP.
/join discord.test
Action Success: Channel discord.test successfully joined.
Server: DisplayName has switched from `discord.general` to `discord.test`.
Server: DisplayName has joined `discord.test` via UDP.
```

### Test Case 6: Handling ERR message

**What was tested:**

The ability to handle an ERR message from the server.

**Why it was tested:**

To verify that the client can correctly interpret and respond to error messages from the server.

**Command:**
```bash
./ipk25chat-client -t tcp -s localhost 
```

**Output:**
```bash
ERROR FROM Server: ERROR
```

**Expected output:**
```bash
ERROR FROM Server: ERROR
```

## Bibliography

- Stevens, W. R., Fenner, B., & Rudoff, A. M. (2003). *UNIX Network Programming: The Sockets Networking API*
- SharpPcap documentation: https://github.com/dotpcap/sharppcap
- RFC 793 - Transmission Control Protocol: https://tools.ietf.org/html/rfc793
- RFC 768 - User Datagram Protocol: https://tools.ietf.org/html/rfc768

