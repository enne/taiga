﻿using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net;
using System.Net.NetworkInformation;

namespace OpenSim.Framework
{
    /// <summary>
    /// Handles NAT translation in a 'manner of speaking'
    /// Allows you to return multiple different external
    /// hostnames depending on the requestors network
    /// 
    /// This enables standard port forwarding techniques
    /// to work correctly with OpenSim.
    /// </summary>
    static class NetworkUtil
    {
        // IPv4Address, Subnet
        static readonly Dictionary<IPAddress,IPAddress> m_subnets = new Dictionary<IPAddress, IPAddress>();

        private static IPAddress GetExternalIPFor(IPAddress destination, string defaultHostname)
        {
            // Adds IPv6 Support (Not that any of the major protocols supports it...)
            if (destination.AddressFamily == AddressFamily.InterNetworkV6)
            {
                foreach (IPAddress host in Dns.GetHostAddresses(defaultHostname))
                {
                    if (host.AddressFamily == AddressFamily.InterNetworkV6)
                        return host;
                }
            }

            if(destination.AddressFamily != AddressFamily.InterNetwork)
                return null;

            // Check if we're accessing localhost.
            foreach (IPAddress host in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (host.Equals(destination))
                    return destination;
            }

            // Check for same LAN segment
            foreach (KeyValuePair<IPAddress, IPAddress> subnet in m_subnets)
            {
                byte[] subnetBytes = subnet.Value.GetAddressBytes();
                byte[] localBytes = subnet.Key.GetAddressBytes();
                byte[] destBytes = destination.GetAddressBytes();
                
                if(subnetBytes.Length != destBytes.Length || subnetBytes.Length != localBytes.Length)
                    return null;

                bool valid = true;

                for(int i=0;i<subnetBytes.Length;i++)
                {
                    if ((localBytes[i] & subnetBytes[i]) != (destBytes[i] & subnetBytes[i]))
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid)
                    return subnet.Key;
            }

            // Check to see if we can find a IPv4 address.
            foreach (IPAddress host in Dns.GetHostAddresses(defaultHostname))
            {
                if (host.AddressFamily == AddressFamily.InterNetwork)
                    return host;
            }

            // Unable to find anything.
            throw new ArgumentException("[NetworkUtil] Unable to resolve defaultHostname to an IPv4 address for an IPv4 client");
        }

        static NetworkUtil()
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation address in ni.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        m_subnets.Add(address.Address, address.IPv4Mask);
                    }
                }
            }
        }

        public static IPAddress GetIPFor(IPEndPoint user, string defaultHostname)
        {
            // Try subnet matching
            IPAddress rtn = GetExternalIPFor(user.Address, defaultHostname);
            if (rtn != null)
                return rtn;

            // Otherwise use the old algorithm
            IPAddress ia;

            if (IPAddress.TryParse(defaultHostname, out ia))
                return ia;

            ia = null;

            foreach (IPAddress Adr in Dns.GetHostAddresses(defaultHostname))
            {
                if (Adr.AddressFamily == AddressFamily.InterNetwork)
                {
                    ia = Adr;
                    break;
                }
            }

            return ia;
        }

        public static string GetHostFor(IPAddress user, string defaultHostname)
        {
            IPAddress rtn = GetExternalIPFor(user, defaultHostname);
            if(rtn != null)
                return rtn.ToString();

            return defaultHostname;
        }
    }
}