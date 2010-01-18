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
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Web;
using DotNetOpenAuth.Messaging;
using DotNetOpenAuth.OAuth.Messages;
using DotNetOpenAuth.OpenId.Provider;
using DotNetOpenAuth.OpenId.RelyingParty;
using log4net;
using Nwc.XmlRpc;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.Communications;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Framework.Communications.Services;
using OpenSim.Framework.Servers.HttpServer;
using OpenMetaverse;
using OpenMetaverse.Http;
using OpenMetaverse.StructuredData;
using CableBeachMessages;

using OAuthConsumer = DotNetOpenAuth.OAuth.WebConsumer;
using CapabilityIdentifier = System.Uri;
using ServiceIdentifier = System.Uri;

namespace OpenSim.Grid.UserServer.Modules
{
    #region Service Classes

    public class CapabilityRequirements : Dictionary<CapabilityIdentifier, Uri> { }
    public class ServiceRequirements : Dictionary<ServiceIdentifier, CapabilityRequirements> { }

    public class ServiceCollection : Dictionary<ServiceIdentifier, Service> { }

    public class ServiceRequestsData
    {
        public Uri UserIdentity;
        public UserProfileData UserProfile;
        public ServiceRequirements ServiceRequirements;
        public ServiceCollection Services;
        public string AuthMethod;

        public ServiceRequestsData(Uri userIdentity, UserProfileData userProfile, ServiceRequirements serviceRequirements, ServiceCollection services,
            string authMethod)
        {
            UserIdentity = userIdentity;
            UserProfile = userProfile;
            ServiceRequirements = serviceRequirements;
            Services = services;
            AuthMethod = authMethod;
        }
    }

    #endregion Service Classes

    #region UserServer Interface

    public class CableBeachLoginService : UserLoginService
    {
        public CableBeachLoginService(UserManagerBase userManager, IInterServiceInventoryServices inventoryService,
            LibraryRootFolder libraryRootFolder, UserConfig config, string welcomeMess, IRegionProfileRouter regionProfileService)
            : base(userManager, inventoryService, libraryRootFolder, config, welcomeMess, regionProfileService)
        {
        }

        public new InventoryData GetInventorySkeleton(UUID userID)
        {
            return base.GetInventorySkeleton(userID);
        }

        public new bool AllowLoginWithoutInventory()
        {
            return base.AllowLoginWithoutInventory();
        }

        public new ArrayList GetLibraryOwner()
        {
            return base.GetLibraryOwner();
        }

        public new ArrayList GetInventoryLibrary()
        {
            return base.GetInventoryLibrary();
        }

        public LoginResponse.BuddyList ConvertFriendListItem(List<FriendListItem> LFL)
        {
            LoginResponse.BuddyList buddylistreturn = new LoginResponse.BuddyList();
            foreach (FriendListItem fl in LFL)
            {
                LoginResponse.BuddyList.BuddyInfo buddyitem = new LoginResponse.BuddyList.BuddyInfo(fl.Friend);
                buddyitem.BuddyID = fl.Friend;
                buddyitem.BuddyRightsHave = (int)fl.FriendListOwnerPerms;
                buddyitem.BuddyRightsGiven = (int)fl.FriendPerms;
                buddylistreturn.AddNewBuddy(buddyitem);
            }

            return buddylistreturn;
        }
    }

    #endregion UserServer Interface

    /// <summary>
    /// Holds persistent (but temporary) state data across requests and different stream handlers
    /// </summary>
    public static class CableBeachState
    {
        /// <summary>Maximum length of time (in milliseconds) that service
        /// information is cached</summary>
        public const int SERVICE_CACHE_TIMEOUT = 1000 * 60 * 10;

        /// <summary>Maximum length of time (in milliseconds) that a service
        /// request is tracked before timing out</summary>
        public const int SERVICE_OAUTH_TIMEOUT = 1000 * 60 * 3;

        /// <summary>Maximum length of time (in milliseconds) that a pending
        /// login request is tracked before dropping it</summary>
        public const int PENDING_LOGIN_TIMEOUT = 1000 * 60 * 10;

        /// <summary>Timeout (in milliseconds) for a request to a seed
        /// capability</summary>
        public const int SEED_CAP_TIMEOUT = 1000 * 30;

        const string LOGIN_TEMPLATE_FILE = "webtemplates/userserver_cablebeachlogin.tpl";
        const string LOGIN_SUCCESS_TEMPLATE_FILE = "webtemplates/userserver_cablebeachloginsuccess.tpl";

        #region Service Requirements

        /// <summary>These capabilities must be retrieved from the asset
        /// service for login to be considered successful</summary>
        static readonly string[] ASSET_REQUIRED_CAPS = new string[] {
            CableBeachServices.ASSET_CREATE_ASSET,
            CableBeachServices.ASSET_GET_ASSET,
            CableBeachServices.ASSET_GET_ASSET_METADATA };

        /// <summary>These capabilities must be retrieved from the filesystem
        /// service for login to be considered successful</summary>
        static readonly string[] FILESYSTEM_REQUIRED_CAPS = new string[] {
            CableBeachServices.FILESYSTEM_CREATE_FILESYSTEM,
            CableBeachServices.FILESYSTEM_CREATE_OBJECT,
            CableBeachServices.FILESYSTEM_GET_ACTIVE_GESTURES,
            CableBeachServices.FILESYSTEM_GET_FILESYSTEM,
            CableBeachServices.FILESYSTEM_GET_FILESYSTEM_SKELETON,
            CableBeachServices.FILESYSTEM_GET_OBJECT,
            CableBeachServices.FILESYSTEM_GET_ROOT_FOLDER,
            CableBeachServices.FILESYSTEM_PURGE_FOLDER,
            CableBeachServices.FILESYSTEM_DELETE_OBJECT,
            CableBeachServices.FILESYSTEM_GET_FOLDER_FOR_TYPE,
            CableBeachServices.FILESYSTEM_GET_FOLDER_CONTENTS };

        #endregion Service Requirements

        /// <summary>Temporary state storage for the OpenID RP</summary>
        public static readonly StandardRelyingPartyApplicationStore RelyingPartyStore;
        /// <summary>OpenID RP</summary>
        public static readonly OpenIdRelyingParty RelyingParty;
        /// <summary>Temporary state storage for the OpenID IDP</summary>
        public static readonly StandardProviderApplicationStore ProviderStore;
        /// <summary>OpenID IDP</summary>
        public static readonly OpenIdProvider Provider;
        /// <summary>Temporary state storage for the OAuth Consumer</summary>
        public static readonly InMemoryTokenManager OAuthTokenManager;

        /// <summary>Caches information about recently discovered services. Maps
        /// the service URL to a service description object</summary>
        public static readonly ExpiringCache<Uri, Service> ServiceCache = new ExpiringCache<Uri, Service>();
        /// <summary>Maps OAuth request tokens to service and requirements data
        /// to track current service capability requests</summary>
        public static readonly ExpiringCache<string, ServiceRequestsData> CurrentServiceRequests = new ExpiringCache<string, ServiceRequestsData>();
        /// <summary>Maps sessionIDs to user profiles for authenticated sessions
        /// that have not logged in with a viewer yet</summary>
        public static readonly ExpiringCache<UUID, UserProfileData> PendingLogins = new ExpiringCache<UUID, UserProfileData>();

        /// <summary>Static instance of the thread-safe XML-RPC deserializing class</summary>
        public static readonly XmlRpcRequestDeserializer XmlRpcLoginDeserializer = new XmlRpcRequestDeserializer();

        /// <summary>Template engine for rendering dynamic webpages</summary>
        public static readonly SmartyEngine WebTemplates = new SmartyEngine();

        /// <summary>A reference to available UserServer services</summary>
        public static CableBeachLoginService LoginService;

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        static CableBeachState()
        {
            RelyingPartyStore = new StandardRelyingPartyApplicationStore();
            RelyingParty = new OpenIdRelyingParty(RelyingPartyStore);
            ProviderStore = new StandardProviderApplicationStore();
            Provider = new OpenIdProvider(ProviderStore);

            OAuthTokenManager = new InMemoryTokenManager(Dns.GetHostName(), null);
        }

        #region Login Methods

        public static void StartLogin(OSHttpRequest httpRequest, OSHttpResponse httpResponse, Uri identity, string firstName, string lastName, string email,
            string authMethod)
        {
            UserProfileData profile;
            if (TryGetOrCreateUser(httpRequest.Url.DnsSafeHost, identity, firstName, lastName, email, authMethod, out profile))
            {
                Service assetService = null, filesystemService = null;

                // Try to fetch a user-specified inventory service. If that fails, fall back on our local inventory service
                if (!TryGetUserFilesystemService(identity, out filesystemService))
                {
                    Uri localFilesystemUri = LoginService.m_config.InventoryUrl;
                    filesystemService = CableBeachState.CreateServiceFromLRDD(localFilesystemUri, new Uri(CableBeachServices.FILESYSTEM), true, true);
                }

                if (filesystemService != null)
                {
                    // Use our local asset service
                    Uri localAssetUri = LoginService.m_config.InventoryUrl;
                    assetService = CableBeachState.CreateServiceFromLRDD(localAssetUri, new Uri(CableBeachServices.ASSETS), true, false);
                }

                if (assetService != null && filesystemService != null)
                {
                    ServiceCollection services = new ServiceCollection();
                    services.Add(new Uri(CableBeachServices.ASSETS), assetService);
                    services.Add(new Uri(CableBeachServices.FILESYSTEM), filesystemService);

                    // Create an empty collection of capability requirements that must be filled in before the login is
                    // considered successful
                    ServiceRequirements serviceRequirements = CableBeachState.CreateServiceRequirements();

                    ServiceRequestsData stateData = new ServiceRequestsData(identity, profile, serviceRequirements, services, authMethod);

                    // Contact the trusted services first
                    GetTrustedCapabilities(identity, stateData);

                    // Start the OAuth loop or return success
                    GetCapabilitiesOrCompleteLogin(httpRequest, httpResponse, stateData, null);
                }
                else
                {
                    SendLoginTemplate(httpResponse, null, "Failed to contact required services for " + identity);
                }
            }
            else
            {
                SendLoginTemplate(httpResponse, null, "Failed to retrieve or create account information for " + identity);
            }
        }

        static bool TryGetUserFilesystemService(Uri identity, out Service filesystemService)
        {
            // FIXME: Do LRDD on the user identity URL to get a list of user-supplied services
            filesystemService = null;
            return false;
        }

        static bool TryGetOrCreateUser(string serverHostname, Uri identity, string firstName, string lastName, string email, string authMethod,
            out UserProfileData profile)
        {
            profile = null;

            if (identity.Host.Equals(serverHostname, StringComparison.InvariantCultureIgnoreCase))
            {
                // This is a local user identity, try to parse the first and last name out
                string[] firstLast = identity.Segments[identity.Segments.Length - 1].Split('_', '.');

                if (firstLast.Length == 2)
                {
                    firstName = firstLast[0];
                    lastName = firstLast[1];

                    profile = LoginService.UserManager.GetUserProfile(firstName, lastName);
                }
            }

            if (profile == null)
            {
                // Try to look this user up by the UUID that would have been created from the identity URL
                profile = LoginService.UserManager.GetUserProfile(CableBeachUtils.IdentityToUUID(identity));
            }

            if (profile == null)
            {
                // No profile found yet, create a new user

                // If no name has been set, use the OpenID URL as a name
                if (String.IsNullOrEmpty(firstName) || String.IsNullOrEmpty(lastName))
                    BuildAuthServiceName(identity, authMethod, out firstName, out lastName);

                // Generate a random password to prevent unwanted login attempts through the non-OpenID path
                string randomPassword = System.IO.Path.GetRandomFileName().Replace(".", String.Empty);

                // Sanity check
                if (email == null)
                    email = String.Empty;

                // The agentID for a Cable Beach login is not random, it is the MD5 hash of the identity URL.
                // Ideally this assumption would not have to be baked in, but it helps out in a few edge cases
                // for the moment
                UUID agentID = CableBeachUtils.IdentityToUUID(identity);

                // Add the new user account
                try
                {
                    UUID newUserID = LoginService.UserManager.AddUser(firstName, lastName, randomPassword, email,
                        LoginService.m_config.DefaultX, LoginService.m_config.DefaultY, agentID);

                    if (newUserID != agentID)
                    {
                        // Creating the account failed. It's possible that the firstName/lastName already exist in
                        // the user database. Try falling back on using the OpenID as the username before giving up
                        BuildAuthServiceName(identity, authMethod, out firstName, out lastName);

                        newUserID = LoginService.UserManager.AddUser(firstName, lastName, randomPassword, email,
                            LoginService.m_config.DefaultX, LoginService.m_config.DefaultY, agentID);

                        if (newUserID != agentID)
                        {
                            m_log.Error("[CABLE BEACH LOGIN]: Failed to create new user \"" + firstName + " " + lastName + "\" (" + agentID + ")");
                            agentID = UUID.Zero;
                        }
                    }
                }
                catch (Exception ex)
                {
                    m_log.Error("[CABLE BEACH LOGIN]: Error while creating new user: " + ex.Message);
                }

                // Try to fetch the newly created profile
                if (agentID != UUID.Zero)
                    profile = LoginService.UserManager.GetUserProfile(agentID);
            }

            return (profile != null);
        }

        public static void GetCapabilitiesOrCompleteLogin(OSHttpRequest httpRequest, OSHttpResponse httpResponse, ServiceRequestsData stateData,
            string previousRequestToken)
        {
            OutgoingWebResponse oauthResponse;
            string requestToken;
            bool success = GetUntrustedCapabilities(httpRequest, stateData, out oauthResponse, out requestToken);

            if (oauthResponse != null)
            {
                // Still acquiring capabilities, repeat the OAuth process
                lock (CableBeachState.CurrentServiceRequests)
                {
                    if (!String.IsNullOrEmpty(previousRequestToken))
                        CableBeachState.CurrentServiceRequests.Remove(previousRequestToken);
                    CableBeachState.CurrentServiceRequests.AddOrUpdate(requestToken, stateData, TimeSpan.FromMilliseconds(CableBeachState.SERVICE_OAUTH_TIMEOUT));
                }

                // OAuth sequence starting, redirect the client
                OpenAuthHelper.OpenAuthResponseToHttp(httpResponse, oauthResponse);
            }
            else if (success)
            {
                // No capabilities need to be acquired, this is a successful login
                if (!String.IsNullOrEmpty(previousRequestToken))
                {
                    lock (CableBeachState.CurrentServiceRequests)
                        CableBeachState.CurrentServiceRequests.Remove(previousRequestToken);
                }

                // Check if this avatar is already logged in. If so, destroy the old session
                if (stateData.UserProfile.CurrentAgent != null && stateData.UserProfile.CurrentAgent.AgentOnline)
                {
                    m_log.Warn("[CABLE BEACH LOGIN]: Logging out previous session for " + stateData.UserProfile.Name);
                    LoginService.LogOffUser(stateData.UserProfile, "This account is logging in from another location");
                }

                // Create a new session
                LoginService.CreateAgent(stateData.UserProfile, new OpenMetaverse.StructuredData.OSD());
                LoginService.CommitAgent(ref stateData.UserProfile);

                // Track the new session in the PendingLogins collection
                UUID sessionID = stateData.UserProfile.CurrentAgent.SessionID;
                CableBeachState.PendingLogins.AddOrUpdate(sessionID, stateData.UserProfile, TimeSpan.FromMilliseconds(CableBeachState.PENDING_LOGIN_TIMEOUT));

                m_log.Info("[CABLE BEACH LOGIN]: Agent session created for " + stateData.UserProfile.Name + ", session_id: " + sessionID);

                // Return the successful login template
                SendLoginSuccessTemplate(httpRequest, httpResponse, stateData.UserIdentity, stateData.UserProfile, sessionID);
            }
            else
            {
                // No OAuth response to send back and there was a failure. This login attempt is done
                SendLoginTemplate(httpResponse, null, "Failed to fetch required capabilities");
            }
        }

        static void GetTrustedCapabilities(Uri identity, ServiceRequestsData stateData)
        {
            m_log.Debug("[CABLE BEACH LOGIN]: Looking for trusted services, " + stateData.ServiceRequirements.Count + "service requirements and " +
                stateData.Services.Count + " services total");

            foreach (KeyValuePair<ServiceIdentifier, CapabilityRequirements> serviceRequirement in stateData.ServiceRequirements)
            {
                // Check if any of the capability requirements are missing capability URLs
                if (serviceRequirement.Value.ContainsValue(null))
                {
                    CapabilityRequirements currentCapsRequirements = serviceRequirement.Value;
                    Service currentService;

                    // Try and get the discovered service that corresponds to this service requirement
                    if (stateData.Services.TryGetValue(serviceRequirement.Key, out currentService))
                    {
                        // Check if this is a trusted service with a valid seed capability
                        if (currentService.IsTrusted && currentService.SeedCapability != null)
                        {
                            #region Seed Capability Request

                            Uri[] capRequests = new Uri[currentCapsRequirements.Count];
                            currentCapsRequirements.Keys.CopyTo(capRequests, 0);

                            m_log.Info("[CABLE BEACH LOGIN]: Requesting " + capRequests.Length + " capabilities from seed capability at " +
                                currentService.SeedCapability);

                            // Trusted capability retrieval through a seed capability
                            RequestCapabilitiesMessage message = new RequestCapabilitiesMessage();
                            message.Identity = identity;
                            message.Capabilities = capRequests;

                            CapsClient request = new CapsClient(currentService.SeedCapability);
                            OSDMap responseMap = request.GetResponse(message.Serialize(), OSDFormat.Json, SEED_CAP_TIMEOUT) as OSDMap;

                            if (responseMap != null)
                            {
                                RequestCapabilitiesReplyMessage reply = new RequestCapabilitiesReplyMessage();
                                reply.Deserialize(responseMap);

                                m_log.Info("[CABLE BEACH LOGIN]: Fetched " + reply.Capabilities.Count + " capabilities from seed capability at " +
                                    currentService.SeedCapability);

                                foreach (KeyValuePair<Uri, Uri> entry in reply.Capabilities)
                                    currentCapsRequirements[entry.Key] = entry.Value;
                            }
                            else
                            {
                                m_log.Error("[CABLE BEACH LOGIN]: Failed to fetch capabilities from seed capability at " + currentService.SeedCapability);
                            }

                            #endregion Seed Capability Request
                        }
                        else
                        {
                            m_log.Debug("[CABLE BEACH LOGIN]: Skipping untrusted service for " + serviceRequirement.Key);
                        }
                    }
                    else
                    {
                        m_log.Warn("[CABLE BEACH LOGIN]: No service discovered for service requirement " + serviceRequirement.Key);
                    }
                }
            }

            /*foreach (KeyValuePair<ServiceIdentifier, Service> serviceEntry in stateData.Services)
            {
                Service service = serviceEntry.Value;

                if (service.IsTrusted && service.SeedCapability == null)
                    m_log.Warn("[CABLE BEACH LOGIN]: Trusted service has an oauth endpoint at " + service.OAuthRequestToken + " but no seed capability");

                // Check if this is a trusted service
                if (service.IsTrusted && service.SeedCapability != null)
                {
                    // Check if this service meets a service requirement
                    CapabilityRequirements capsRequirements;
                    if (stateData.ServiceRequirements.TryGetValue(serviceEntry.Key, out capsRequirements))
                    {
                        #region Seed Capability Request

                        Uri[] capRequests = new Uri[capsRequirements.Count];
                        capsRequirements.Keys.CopyTo(capRequests, 0);

                        m_log.Info("[CABLE BEACH LOGIN]: Requesting " + capRequests.Length + " capabilities from seed capability at " + service.SeedCapability);

                        // Trusted capability retrieval through a seed capability
                        RequestCapabilitiesMessage message = new RequestCapabilitiesMessage();
                        message.Identity = identity;
                        message.Capabilities = capRequests;

                        CapsClient request = new CapsClient(service.SeedCapability);
                        OSDMap responseMap = request.GetResponse(message.Serialize(), OSDFormat.Json, SEED_CAP_TIMEOUT) as OSDMap;

                        if (responseMap != null)
                        {
                            RequestCapabilitiesReplyMessage reply = new RequestCapabilitiesReplyMessage();
                            reply.Deserialize(responseMap);

                            m_log.Info("[CABLE BEACH LOGIN]: Fetched " + reply.Capabilities.Count + " capabilities from seed capability at " + service.SeedCapability);

                            foreach (KeyValuePair<Uri, Uri> entry in reply.Capabilities)
                                capsRequirements[entry.Key] = entry.Value;
                        }
                        else
                        {
                            m_log.Error("[CABLE BEACH LOGIN]: Failed to fetch capabilities from seed capability at " + service.SeedCapability);
                        }

                        #endregion Seed Capability Request
                    }
                    else
                    {
                        m_log.Warn("[CABLE BEACH LOGIN]: Trusted service at " + service.SeedCapability + " does not meet any service requirements");
                    }
                }
            }*/
        }

        static bool GetUntrustedCapabilities(OSHttpRequest httpRequest, ServiceRequestsData stateData, out OutgoingWebResponse webResponse, out string requestToken)
        {
            webResponse = null;
            requestToken = null;

            ServiceIdentifier currentServiceIdentifier = GetCurrentService(stateData.ServiceRequirements);
            if (currentServiceIdentifier != null)
            {
                CapabilityRequirements currentCapsRequirements;
                Service currentService;

                if (stateData.ServiceRequirements.TryGetValue(currentServiceIdentifier, out currentCapsRequirements) &&
                    stateData.Services.TryGetValue(currentServiceIdentifier, out currentService))
                {
                    if (
                        currentService.OAuthGetAccessToken != null &&
                        currentService.OAuthRequestToken != null &&
                        currentService.OAuthAuthorizeToken != null)
                    {
                        #region OAuth Request

                        // Untrusted capability retrieval through OAuth
                        try
                        {
                            // Create a comma-separated list of capabilities to request
                            int i = 0;
                            string[] capRequests = new string[currentCapsRequirements.Count];
                            foreach (Uri cap in currentCapsRequirements.Keys)
                                capRequests[i++] = cap.ToString();
                            string capRequest = String.Join(",", capRequests);

                            m_log.Info("[CABLE BEACH LOGIN]: Requesting " + capRequests.Length + " capabilities from OAuth endpoint at " + currentService.OAuthRequestToken);

                            OAuthConsumer consumer = new OAuthConsumer(OpenAuthHelper.CreateServiceProviderDescription(currentService), CableBeachState.OAuthTokenManager);

                            Dictionary<string, string> extraData = new Dictionary<string, string>();
                            extraData["cb_identity"] = stateData.UserIdentity.ToString();
                            extraData["cb_auth_method"] = stateData.AuthMethod;
                            extraData["cb_capabilities"] = capRequest;

                            UserAuthorizationRequest authorizationRequest = consumer.PrepareRequestUserAuthorization(
                                new Uri(httpRequest.Url, "/login/oauth_callback"), null, extraData);
                            webResponse = consumer.Channel.PrepareResponse(authorizationRequest);

                            if (webResponse != null)
                            {
                                requestToken = authorizationRequest.RequestToken;
                                return true;
                            }
                            else
                            {
                                // Uh oh, something unexpected happened building the OAuth redirect
                                m_log.Error("[CABLE BEACH LOGIN]: Failed to initiate OAuth sequence at " + currentService.OAuthRequestToken + ", could not prepare a redirect");
                                return false;
                            }
                        }
                        catch (Exception ex)
                        {
                            m_log.Error("[CABLE BEACH LOGIN]: Failed to initiate OAuth sequence at " + currentService.OAuthRequestToken + ": " + ex.Message);
                            return false;
                        }

                        #endregion OAuth Request
                    }
                    else
                    {
                        m_log.Error("[CABLE BEACH LOGIN]: Incomplete service definition retrieved from " + currentService.XrdDocument + ", flushing from the cache");
                        CableBeachState.ServiceCache.Remove(currentService.XrdDocument);
                        return false;
                    }
                }
                else
                {
                    m_log.Error("[CABLE BEACH LOGIN]: Login for " + stateData.UserIdentity + " did not discover a service for service identifier " +
                        currentServiceIdentifier);
                    return false;
                }
            }
            else
            {
                m_log.Info("[CABLE BEACH LOGIN]: All service requirements have been met for " + stateData.UserIdentity);
                return true;
            }
        }

        #endregion Login Methods

        #region Helper Methods

        public static bool IsIdentityAuthorized(Uri identity)
        {
            // FIXME: Implement whitelist/blacklist support for OpenID URLs
            return true;
        }

        public static ServiceRequirements CreateServiceRequirements()
        {
            ServiceRequirements requirements = new ServiceRequirements();

            // Asset service
            {
                CapabilityRequirements assetRequirements = new CapabilityRequirements();
                for (int i = 0; i < ASSET_REQUIRED_CAPS.Length; i++)
                    assetRequirements.Add(new Uri(ASSET_REQUIRED_CAPS[i]), null);
                requirements.Add(new Uri(CableBeachServices.ASSETS), assetRequirements);
            }

            // Filesystem service
            {
                CapabilityRequirements filesystemRequirements = new CapabilityRequirements();
                for (int i = 0; i < FILESYSTEM_REQUIRED_CAPS.Length; i++)
                    filesystemRequirements.Add(new Uri(FILESYSTEM_REQUIRED_CAPS[i]), null);
                requirements.Add(new Uri(CableBeachServices.FILESYSTEM), filesystemRequirements);
            }

            return requirements;
        }

        public static Service CreateServiceFromLRDD(Uri serviceLocation, ServiceIdentifier serviceType, bool isTrusted, bool allowOverride)
        {
            Service service;

            // Cache check
            if (ServiceCache.TryGetValue(serviceLocation, out service))
                return service;

            // Fetch and parse the XRD
            service = XrdHelper.CreateServiceFromLRDD(serviceLocation, serviceType, isTrusted, allowOverride);

            // Cache the results
            if (service != null)
                ServiceCache.AddOrUpdate(serviceLocation, service, TimeSpan.FromMilliseconds(SERVICE_CACHE_TIMEOUT));

            return service;
        }

        static void BuildAuthServiceName(Uri identity, string authMethod, out string firstName, out string lastName)
        {
            firstName = identity.ToString();
            // Shorten long OpenID URLs
            if (firstName.Length > 32)
                firstName = firstName.Substring(0, 15) + "..." + firstName.Substring(firstName.Length - 14);

            switch (authMethod)
            {
                case CableBeachAuthMethods.OPENID:
                    lastName = "OpenID";
                    break;
                case CableBeachAuthMethods.FACEBOOK:
                    lastName = "Facebook";
                    break;
                default:
                    lastName = "CableBeach";
                    break;
            }
        }

        public static ServiceIdentifier GetCurrentService(ServiceRequirements serviceRequirements)
        {
            // Iterate over each service requirement
            foreach (KeyValuePair<ServiceIdentifier, CapabilityRequirements> serviceRequirement in serviceRequirements)
            {
                if (serviceRequirement.Value.ContainsValue(null))
                    return serviceRequirement.Key;
            }

            return null;
        }

        #endregion Helper Methods

        #region HTML Templates

        public static void SendLoginTemplate(OSHttpResponse httpResponse, string infoMessage, string errorMessage)
        {
            string output = null;
            Dictionary<string, object> variables = new Dictionary<string, object>();
            variables["has_message"] = !String.IsNullOrEmpty(infoMessage);
            variables["has_error"] = !String.IsNullOrEmpty(errorMessage);
            variables["message"] = infoMessage ?? String.Empty;
            variables["error"] = errorMessage ?? String.Empty;

            try { output = CableBeachState.WebTemplates.Render(LOGIN_TEMPLATE_FILE, variables); }
            catch (Exception) { }
            if (output == null) { output = "Failed to render template " + LOGIN_TEMPLATE_FILE; }

            httpResponse.ContentType = "text/html";
            OpenAuthHelper.AddToBody(httpResponse, output);
        }

        public static void SendLoginSuccessTemplate(OSHttpRequest httpRequest, OSHttpResponse httpResponse, Uri identity, UserProfileData profile, UUID sessionID)
        {
            string loginUri = new Uri(httpRequest.Url, "/login/" + sessionID.ToString()).ToString();
            string cbLoginUri = "cablebeach://" + HttpUtility.UrlEncode(loginUri);

            string output = null;
            Dictionary<string, object> variables = new Dictionary<string, object>();
            variables["identity"] = identity.ToString();
            variables["login_uri"] = loginUri;
            variables["cb_login_uri"] = cbLoginUri;

            try { output = CableBeachState.WebTemplates.Render(LOGIN_SUCCESS_TEMPLATE_FILE, variables); }
            catch (Exception) { }
            if (output == null) { output = "Failed to render template " + LOGIN_SUCCESS_TEMPLATE_FILE; }

            // TODO: Put the sessionID in a response cookie
            httpResponse.ContentType = "text/html";
            OpenAuthHelper.AddToBody(httpResponse, output);
        }

        #endregion HTML Templates
    }
}