using FWO.Basics;
using FWO.Basics.Comparer;
using NetTools;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace FWO.ExternalSystems.FirewallWrite
{
    /// <summary>
    /// Compares an object a Check Point create call should create with the object of the same name read from the
    /// management, as the existing Check Point ticket handling does: hosts, networks and address ranges must have the
    /// requested addresses, a group only has to exist. Missing or unreadable values never count as equal.
    /// </summary>
    public static class CheckPointObjectComparison
    {
        private const int kBitsPerByte = 8;

        /// <summary>Whether the existing host has the requested address.</summary>
        /// <param name="requested">Body of the add-host call.</param>
        /// <param name="existing">Answer of show-host.</param>
        public static bool HostMatches(JsonNode? requested, JsonNode? existing)
        {
            IPAddress? requestedAddress = ParseAddress(Text(requested, "ip-address"));
            IPAddress? existingAddress = ParseAddress(Text(existing, "ipv4-address") ?? Text(existing, "ipv6-address"));
            return requestedAddress != null && existingAddress != null && new IPAdressComparer().Compare(existingAddress, requestedAddress) == 0;
        }

        /// <summary>Whether the existing network has the requested subnet and mask length.</summary>
        /// <param name="requested">Body of the add-network call.</param>
        /// <param name="existing">Answer of show-network.</param>
        public static bool NetworkMatches(JsonNode? requested, JsonNode? existing)
        {
            IPAddress? requestedSubnet = ParseAddress(Text(requested, "subnet"));
            int? requestedMaskLength = Number(requested, "mask-length");
            if (requestedSubnet == null || requestedMaskLength == null || existing == null)
            {
                return false;
            }
            bool isIpv6 = requestedSubnet.AddressFamily == AddressFamily.InterNetworkV6;
            IPAddress? existingSubnet = ParseAddress(isIpv6 ? Text(existing, "subnet6") : Text(existing, "subnet4") ?? Text(existing, "subnet"));
            int? existingMaskLength = isIpv6 ? Number(existing, "mask-length6") : Number(existing, "mask-length4") ?? Number(existing, "mask-length");
            if (!isIpv6 && existingMaskLength == null)
            {
                existingMaskLength = PrefixLength(ParseAddress(Text(existing, "subnet-mask")));
            }
            return existingSubnet != null && existingMaskLength == requestedMaskLength
                && new IPAdressComparer().Compare(existingSubnet, requestedSubnet) == 0;
        }

        /// <summary>Whether the existing address range has the requested first and last address.</summary>
        /// <param name="requested">Body of the add-address-range call.</param>
        /// <param name="existing">Answer of show-address-range.</param>
        public static bool AddressRangeMatches(JsonNode? requested, JsonNode? existing)
        {
            IPAddress? requestedFirst = ParseAddress(Text(requested, "ip-address-first"));
            IPAddress? requestedLast = ParseAddress(Text(requested, "ip-address-last"));
            IPAddress? existingFirst = ParseAddress(Text(existing, "ipv4-address-first") ?? Text(existing, "ipv6-address-first") ?? Text(existing, "ip-address-first"));
            IPAddress? existingLast = ParseAddress(Text(existing, "ipv4-address-last") ?? Text(existing, "ipv6-address-last") ?? Text(existing, "ip-address-last"));
            if (requestedFirst == null || requestedLast == null || existingFirst == null || existingLast == null)
            {
                return false;
            }
            IPAddressRange requestedRange = new(requestedFirst, requestedLast);
            IPAddressRange existingRange = new(existingFirst, existingLast);
            return new IPAddressRangeComparer().Compare(existingRange, requestedRange) == 0;
        }

        private static string? Text(JsonNode? node, string propertyName)
        {
            return node?[propertyName] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;
        }

        private static int? Number(JsonNode? node, string propertyName)
        {
            if (node?[propertyName] is not JsonValue value)
            {
                return null;
            }
            if (value.TryGetValue(out int number))
            {
                return number;
            }
            return value.TryGetValue(out string? text) && int.TryParse(text, out int parsed) ? parsed : null;
        }

        private static IPAddress? ParseAddress(string? text)
        {
            return text != null && IPAddress.TryParse(text.StripOffNetmask(), out IPAddress? address) ? address : null;
        }

        private static int? PrefixLength(IPAddress? subnetMask)
        {
            if (subnetMask == null)
            {
                return null;
            }
            int prefixLength = 0;
            foreach (byte maskByte in subnetMask.GetAddressBytes())
            {
                for (int bit = 0; bit < kBitsPerByte; bit++)
                {
                    prefixLength += (maskByte >> bit) & 1;
                }
            }
            return prefixLength;
        }
    }
}
