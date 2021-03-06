﻿/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Grid.UserServer.Modules
{
    public class Service
    {
        public Uri XrdDocument;
        public Uri SeedCapability;
        public Uri OAuthRequestToken;
        public Uri OAuthAuthorizeToken;
        public Uri OAuthGetAccessToken;
        public bool CanOverride;

        public Service(Uri xrdDocument, Uri seedCapability, Uri oAuthRequestToken, Uri oAuthAuthorizeToken, Uri oAuthGetAccessToken, bool canOverride)
        {
            XrdDocument = xrdDocument;
            SeedCapability = seedCapability;
            OAuthRequestToken = oAuthRequestToken;
            OAuthAuthorizeToken = oAuthAuthorizeToken;
            OAuthGetAccessToken = oAuthGetAccessToken;
            CanOverride = canOverride;
        }

        public Service(Service service)
        {
            XrdDocument = service.XrdDocument;
            SeedCapability = service.SeedCapability;
            OAuthRequestToken = service.OAuthRequestToken;
            OAuthAuthorizeToken = service.OAuthAuthorizeToken;
            OAuthGetAccessToken = service.OAuthGetAccessToken;
            CanOverride = service.CanOverride;
        }

        public override string ToString()
        {
            string location;
            bool trusted = (SeedCapability != null);

            if (trusted)
            {
                location = SeedCapability.ToString();
            }
            else
            {
                location = (OAuthGetAccessToken != null)
                    ? OAuthGetAccessToken.ToString()
                    : "null";
            }

            return String.Format("Location: {1}, {2} {3}",
                location,
                trusted ? "Trusted" : "Untrusted",
                CanOverride ? "CanOverride" : "NoOverride");
        }
    }
}
