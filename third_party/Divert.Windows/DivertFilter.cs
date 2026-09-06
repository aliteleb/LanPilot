using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Divert.Windows;

internal record struct ReplaceParenthesesOperation(string Expression);

/// <summary>
/// Helper type to build WinDivert filter expressions.
/// </summary>
public partial class DivertFilter
{
    /// <summary>
    /// The filter clause.
    /// </summary>
    public string Clause { get; }

    /// <summary>
    /// Creates a new filter with the specified clause.
    /// </summary>
    /// <param name="clause">The filter clause.</param>
    public DivertFilter(string clause)
    {
        ArgumentNullException.ThrowIfNull(clause);

        Clause = clause;
    }

    // e.g. "(a and (b or c)) or d" -> "() or d"
    private static string CollapseParentheses(string expression)
    {
        var builder = new StringBuilder();
        int index = 0;
        while (index < expression.Length)
        {
            char c = expression[index];
            if (c == ')')
            {
                var operation = new ReplaceParenthesesOperation(expression);
                throw new InvalidOperationException(operation.ToString());
            }
            else if (c == '(')
            {
                int stack = 1;
                while (stack > 0)
                {
                    index += 1;
                    if (index >= expression.Length)
                    {
                        var operation = new ReplaceParenthesesOperation(expression);
                        throw new InvalidOperationException(operation.ToString());
                    }
                    if (expression[index] == '(')
                    {
                        stack += 1;
                    }
                    else if (expression[index] == ')')
                    {
                        stack -= 1;
                    }
                }
                builder.Append('(');
                builder.Append(')');
            }
            else
            {
                builder.Append(c);
            }
            index += 1;
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"\s(or|\|\|)\s")]
    private static partial Regex OrPatternRegex();

    private static bool MatchOrPattern(string s) => OrPatternRegex().IsMatch(CollapseParentheses(s));

    [GeneratedRegex(@"\s(and|&&)\s")]
    private static partial Regex AndPatternRegex();

    private static bool MatchAndPattern(string s) => AndPatternRegex().IsMatch(CollapseParentheses(s));

    /// <summary>
    /// Combines two filters with an AND operation.
    /// </summary>
    public static DivertFilter operator &(DivertFilter left, DivertFilter right)
    {
        string leftClause = left.Clause;
        string rightClause = right.Clause;
        if (MatchOrPattern(leftClause))
        {
            leftClause = $"({left})";
        }
        if (MatchOrPattern(rightClause))
        {
            rightClause = $"({right})";
        }
        string clause = $"{leftClause} and {rightClause}";
        return new DivertFilter(clause);
    }

    /// <summary>
    /// Combines two filters with an OR operation.
    /// </summary>
    public static DivertFilter operator |(DivertFilter left, DivertFilter right)
    {
        string leftClause = left.Clause;
        string rightClause = right.Clause;
        if (MatchAndPattern(leftClause))
        {
            leftClause = $"({left})";
        }
        if (MatchAndPattern(rightClause))
        {
            rightClause = $"({right})";
        }
        string clause = $"{leftClause} or {rightClause}";
        return new DivertFilter(clause);
    }

    /// <summary>
    /// Returns the filter clause.
    /// </summary>
    /// <returns>The filter clause.</returns>
    public override string ToString()
    {
        return Clause;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj switch
        {
            null => false,
            DivertFilter filter => Clause == filter.Clause,
            string s => Clause == s,
            _ => false,
        };
    }

    /// <inheritdoc />
    public override int GetHashCode() => Clause.GetHashCode();

    /// <summary>
    /// A layer specific property for matching packets/events.
    /// </summary>
    public class Field
    {
        private readonly string field;

        /// <summary>
        /// Creates a new field with the specified name.
        /// </summary>
        /// <param name="field">The field name.</param>
        public Field(string field)
        {
            ArgumentNullException.ThrowIfNull(field);

            this.field = field;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj switch
            {
                null => false,
                Field filter => field == filter.field,
                string s => field == s,
                _ => false,
            };
        }

        /// <inheritdoc />
        public override int GetHashCode() => field.GetHashCode();

        /// <summary>
        /// Returns the field name.
        /// </summary>
        /// <returns>The field name.</returns>
        public override string ToString() => field;

        /// <summary>
        /// Implicitly converts a string to a Field.
        /// </summary>
        /// <param name="field">The field name.</param>
        public static implicit operator Field(string field)
        {
            return new Field(field);
        }

        /// <summary>
        /// Implicitly converts a Field to a DivertFilter.
        /// </summary>
        public static implicit operator DivertFilter(Field field)
        {
            return new DivertFilter(field.ToString());
        }

        /// <summary>
        /// Combines two fields with an AND operation.
        /// </summary>
        public static DivertFilter operator &(Field left, Field right)
        {
            DivertFilter value = left;
            return value & right;
        }

        /// <summary>
        /// Combines two fields with an OR operation.
        /// </summary>
        public static DivertFilter operator |(Field left, Field right)
        {
            DivertFilter value = left;
            return value | right;
        }

        /// <summary>
        /// Negates a field.
        /// </summary>
        public static DivertFilter operator !(Field value)
        {
            string clause = $"not {value}";
            return new DivertFilter(clause);
        }

        private static string Macro(bool value) => value ? "TRUE" : "FALSE";

        private static string Macro(ProtocolType protocolType) =>
            protocolType switch
            {
                ProtocolType.Tcp => "TCP",
                ProtocolType.Udp => "UDP",
                ProtocolType.Icmp => "ICMP",
                ProtocolType.IcmpV6 => "ICMPV6",
                _ => ((int)protocolType).ToString(),
            };

        private static string Macro(DivertEvent e) =>
            e switch
            {
                DivertEvent.NetworkPacket => "PACKET",
                DivertEvent.FlowEstablished => "ESTABLISHED",
                DivertEvent.FlowDeleted => "DELETED",
                DivertEvent.SocketBind => "BIND",
                DivertEvent.SocketConnect => "CONNECT",
                DivertEvent.SocketListen => "LISTEN",
                DivertEvent.SocketAccept => "ACCEPT",
                DivertEvent.ReflectOpen => "OPEN",
                DivertEvent.SocketClose or DivertEvent.ReflectClose => "CLOSE",
                _ => e.ToString(),
            };

        private static string Macro(DivertLayer layer) =>
            layer switch
            {
                DivertLayer.Network => "NETWORK",
                DivertLayer.Forward => "NETWORK_FORWARD",
                DivertLayer.Flow => "FLOW",
                DivertLayer.Socket => "SOCKET",
                DivertLayer.Reflect => "REFLECT",
                _ => layer.ToString(),
            };

        /// <summary>
        /// Equality operator for Field and object.
        /// </summary>
        public static DivertFilter operator ==(Field left, object right)
        {
            string clause = $"{left} = {right}";
            return new DivertFilter(clause);
        }

        /// <summary>
        /// Inequality operator for Field and object.
        /// </summary>
        public static DivertFilter operator !=(Field left, object right)
        {
            string clause = $"{left} != {right}";
            return new DivertFilter(clause);
        }

        /// <summary>
        /// Equality operator for Field and bool.
        /// </summary>
        public static DivertFilter operator ==(Field left, bool right) => left == Macro(right);

        /// <summary>
        /// Inequality operator for Field and bool.
        /// </summary>
        public static DivertFilter operator !=(Field left, bool right) => left != Macro(right);

        /// <summary>
        /// Equality operator for Field and <see cref="ProtocolType"/>.
        /// </summary>
        public static DivertFilter operator ==(Field left, ProtocolType right) => left == Macro(right);

        /// <summary>
        /// Inequality operator for Field and <see cref="ProtocolType"/>.
        /// </summary>
        public static DivertFilter operator !=(Field left, ProtocolType right) => left != Macro(right);

        /// <summary>
        /// Equality operator for Field and <see cref="DivertEvent"/>.
        /// </summary>
        public static DivertFilter operator ==(Field left, DivertEvent right) => left == Macro(right);

        /// <summary>
        /// Inequality operator for Field and <see cref="DivertEvent"/>.
        /// </summary>
        public static DivertFilter operator !=(Field left, DivertEvent right) => left != Macro(right);

        /// <summary>
        /// Equality operator for Field and <see cref="DivertLayer"/>.
        /// </summary>
        public static DivertFilter operator ==(Field left, DivertLayer right) => left == Macro(right);

        /// <summary>
        /// Inequality operator for Field and <see cref="DivertLayer"/>.
        /// </summary>
        public static DivertFilter operator !=(Field left, DivertLayer right) => left != Macro(right);

        /// <summary>
        /// Less than operator for Field and object.
        /// </summary>
        public static DivertFilter operator <(Field left, object right)
        {
            string clause = $"{left} < {right}";
            return new DivertFilter(clause);
        }

        /// <summary>
        /// Greater than operator for Field and object.
        /// </summary>
        public static DivertFilter operator >(Field left, object right)
        {
            string clause = $"{left} > {right}";
            return new DivertFilter(clause);
        }

        /// <summary>
        /// Less than or equal operator for Field and object.
        /// </summary>
        public static DivertFilter operator <=(Field left, object right)
        {
            string clause = $"{left} <= {right}";
            return new DivertFilter(clause);
        }

        /// <summary>
        /// Greater than or equal operator for Field and object.
        /// </summary>
        public static DivertFilter operator >=(Field left, object right)
        {
            string clause = $"{left} >= {right}";
            return new DivertFilter(clause);
        }
    }

    /// <summary>
    /// The true expression.
    /// </summary>
    public static DivertFilter True { get; } = "true";

    /// <summary>
    /// The false expression.
    /// </summary>
    public static DivertFilter False { get; } = "false";

    /// <summary>
    /// The value zero.
    /// </summary>
    public static Field Zero { get; } = "zero";

    /// <summary>
    /// The packet/event timestamp.
    /// </summary>
    public static Field Timestamp { get; } = "timestamp";

    /// <summary>
    /// The event type.
    /// </summary>
    public static Field Event { get; } = "event";

    /// <summary>
    /// Is outbound?
    /// </summary>
    public static Field Outbound { get; } = "outbound";

    /// <summary>
    /// Is inbound?
    /// </summary>
    public static Field Inbound { get; } = "inbound";

    /// <summary>
    /// The interface index.
    /// </summary>
    public static Field InterfaceIndex { get; } = "ifIdx";

    /// <summary>
    /// The sub-interface index.
    /// </summary>
    public static Field SubInterfaceIndex { get; } = "subIfIdx";

    /// <summary>
    /// Is loopback packet?
    /// </summary>
    public static Field Loopback { get; } = "loopback";

    /// <summary>
    /// Is impostor packet?
    /// </summary>
    public static Field Impostor { get; } = "impostor";

    /// <summary>
    /// Is IPv4 fragment?
    /// </summary>
    public static Field Fragment { get; } = "fragment";

    /// <summary>
    /// The endpoint ID.
    /// </summary>
    public static Field EndpointId { get; } = "endpointId";

    /// <summary>
    /// The parent endpoint ID.
    /// </summary>
    public static Field ParentEndpointId { get; } = "parentEndpointId";

    /// <summary>
    /// The process ID.
    /// </summary>
    public static Field ProcessId { get; } = "processId";

    /// <summary>
    /// 8-bit random number.
    /// </summary>
    public static Field Random8 { get; } = "random8";

    /// <summary>
    /// 16-bit random number.
    /// </summary>
    public static Field Random16 { get; } = "random16";

    /// <summary>
    /// 32-bit random number.
    /// </summary>
    public static Field Random32 { get; } = "random32";

    /// <summary>
    /// The layer of the WinDivert handle.
    /// </summary>
    public static Field Layer { get; } = "layer";

    /// <summary>
    /// The priority of the WinDivert handle.
    /// </summary>
    public static Field Priority { get; } = "priority";

    /// <summary>
    /// The i-th byte of the packet.
    /// </summary>
    public static Field Packet(int i) => $"packet[{i}]";

    /// <summary>
    /// The i-th 16-bit word of the packet.
    /// </summary>
    public static Field Packet16(int i) => $"packet16[{i}]";

    /// <summary>
    /// The i-th 32-bit word of the packet.
    /// </summary>
    public static Field Packet32(int i) => $"packet32[{i}]";

    /// <summary>
    /// The packet length.
    /// </summary>
    public static Field Length { get; } = "length";

    /// <summary>
    /// Is IPv4?
    /// </summary>
    public static Field Ip { get; } = "ip";

    /// <summary>
    /// Is IPv6?
    /// </summary>
    public static Field Ipv6 { get; } = "ipv6";

    /// <summary>
    /// Is ICMP?
    /// </summary>
    public static Field ICMP { get; } = "icmp";

    /// <summary>
    /// Is ICMPv6?
    /// </summary>
    // spell-checker:ignore icmpv6
    public static Field ICMPv6 { get; } = "icmpv6";

    /// <summary>
    /// Is TCP?
    /// </summary>
    public static Field TCP { get; } = "tcp";

    /// <summary>
    /// Is UDP?
    /// </summary>
    public static Field UDP { get; } = "udp";

    /// <summary>
    /// The protocol.
    /// </summary>
    public static Field Protocol { get; } = "protocol";

    /// <summary>
    /// The local address.
    /// </summary>
    public static Field LocalAddress { get; } = "localAddr";

    /// <summary>
    /// The local port.
    /// </summary>
    public static Field LocalPort { get; } = "localPort";

    /// <summary>
    /// The remote address.
    /// </summary>
    public static Field RemoteAddress { get; } = "remoteAddr";

    /// <summary>
    /// The remote port.
    /// </summary>
    public static Field RemotePort { get; } = "remotePort";

    /// <summary>
    /// Implicitly converts a string to a <see cref="DivertFilter"/>.
    /// </summary>
    public static implicit operator DivertFilter(string clause)
    {
        return new DivertFilter(clause);
    }

    /// <summary>
    /// Implicitly converts a bool to a <see cref="DivertFilter"/>.
    /// </summary>
    public static implicit operator DivertFilter(bool value)
    {
        return value ? True : False;
    }
}
