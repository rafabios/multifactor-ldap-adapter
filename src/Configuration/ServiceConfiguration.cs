﻿//Copyright(c) 2021 MultiFactor
//Please see licence at 
//https://github.com/MultifactorLab/multifactor-ldap-adapter/blob/main/LICENSE.md

using MultiFactor.Ldap.Adapter.Core;
using NetTools;
using Serilog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace MultiFactor.Ldap.Adapter.Configuration
{
    /// <summary>
    /// Service configuration
    /// </summary>
    public class ServiceConfiguration
    {
        /// <summary>
        /// List of clients with identification by client ip
        /// </summary>
        private IDictionary<IPAddress, ClientConfiguration> _ipClients;

        public ServiceConfiguration()
        {
            _ipClients = new Dictionary<IPAddress, ClientConfiguration>();
        }

        private void AddClient(IPAddress ip, ClientConfiguration client)
        {
            if (_ipClients.ContainsKey(ip))
            {
                throw new ConfigurationErrorsException($"Client with IP {ip} already added from {_ipClients[ip].Name}.config");
            }
            _ipClients.Add(ip, client);
        }

        public ClientConfiguration GetClient(IPAddress ip)
        {
            if (SingleClientMode)
            {
                return _ipClients[IPAddress.Any];
            }
            if (_ipClients.ContainsKey(ip))
            {
                return _ipClients[ip];
            }
            return null;
        }

        public ILdapServerConfig ServerConfig { get; private set; }

        /// <summary>
        /// Multifactor API URL
        /// </summary>
        public string ApiUrl { get; set; }
        /// <summary>
        /// HTTP Proxy for API
        /// </summary>
        public string ApiProxy { get; set; }

        /// <summary>
        /// Logging level
        /// </summary>
        public string LogLevel { get; set; }

        /// <summary>
        /// Certificate for TLS
        /// </summary>
        public X509Certificate2 X509Certificate { get; set; }

        public bool SingleClientMode { get; set; }
        public RandomWaiterConfig InvalidCredentialDelay { get; private set; }



        /// <summary>
        /// Read and load settings from appSettings configuration section
        /// </summary>
        public static ServiceConfiguration Load(ILogger logger)
        {
            var serviceConfig = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);

            var appSettingsSection = serviceConfig.GetSection("appSettings");
            var appSettings = appSettingsSection as AppSettingsSection;

            var apiUrlSetting                   = appSettings.Settings["multifactor-api-url"]?.Value;
            var apiProxySetting                 = appSettings.Settings["multifactor-api-proxy"]?.Value;
            var logLevelSetting                 = appSettings.Settings["logging-level"]?.Value;

            if (string.IsNullOrEmpty(apiUrlSetting))
            {
                throw new Exception("Configuration error: 'multifactor-api-url' element not found");
            }
            if (string.IsNullOrEmpty(logLevelSetting))
            {
                throw new Exception("Configuration error: 'logging-level' element not found");
            }

            var configuration = new ServiceConfiguration
            {
                ApiUrl = apiUrlSetting,
                ApiProxy = apiProxySetting,
                LogLevel = logLevelSetting,
            };

            var ldapServerConfig = LdapServerConfig.Parse(appSettings);
            if (ldapServerConfig.IsEmpty)
            {
                throw new Exception("Configuration error: Neither 'adapter-ldap-endpoint' or 'adapter-ldaps-endpoint' configured");
            }
            configuration.ServerConfig = ldapServerConfig;

            try
            {
                configuration.InvalidCredentialDelay = RandomWaiterConfig.Create(appSettings.Settings[Core.Constants.Configuration.PciDss.InvalidCredentialDelay]?.Value);
            }
            catch
            {
                throw new Exception($"Configuration error: Can't parse '{Core.Constants.Configuration.PciDss.InvalidCredentialDelay}' value");
            }

            var clientConfigFilesPath = Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory) + Path.DirectorySeparatorChar + "clients";
            var clientConfigFiles = Directory.Exists(clientConfigFilesPath) ? Directory.GetFiles(clientConfigFilesPath, "*.config") : new string[0];

            if (clientConfigFiles.Length == 0)
            {
                //check if we have anything
                var ldapServer = appSettings.Settings["ldap-server"]?.Value;
                if (ldapServer == null)
                {
                    throw new ConfigurationErrorsException("No clients' config files found. Use one of the *.template files in the /clients folder to customize settings. Then save this file as *.config.");
                }

                var userNameTransformRulesSection = ConfigurationManager.GetSection("UserNameTransformRules") as UserNameTransformRulesSection;

                var client = Load("General", appSettings, userNameTransformRulesSection);
                configuration.AddClient(IPAddress.Any, client);
                configuration.SingleClientMode = true;
            }
            else
            {
                foreach (var clientConfigFile in clientConfigFiles)
                {
                    logger.Information($"Loading client configuration from {Path.GetFileName(clientConfigFile)}");

                    var customConfigFileMap = new ExeConfigurationFileMap();
                    customConfigFileMap.ExeConfigFilename = clientConfigFile;

                    var config = ConfigurationManager.OpenMappedExeConfiguration(customConfigFileMap, ConfigurationUserLevel.None);
                    var clientSettings = (AppSettingsSection)config.GetSection("appSettings");
                    var userNameTransformRulesSection = config.GetSection("UserNameTransformRules") as UserNameTransformRulesSection;

                    var client = Load(Path.GetFileNameWithoutExtension(clientConfigFile), clientSettings, userNameTransformRulesSection);

                    var ldapClientIpSetting = clientSettings.Settings["ldap-client-ip"]?.Value;
                    if (string.IsNullOrEmpty(ldapClientIpSetting))
                    {
                        throw new Exception("Configuration error: 'ldap-client-ip' element not found");
                    }

                    var elements = ldapClientIpSetting.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var element in elements)
                    {
                        foreach (var ip in IPAddressRange.Parse(element))
                        {
                            configuration.AddClient(ip, client);
                        }
                    }
                }
            }


            return configuration;
        }

        private static ClientConfiguration Load(string name, AppSettingsSection appSettings, UserNameTransformRulesSection userNameTransformRulesSection)
        {
            var ldapServerSetting                               = appSettings.Settings["ldap-server"]?.Value;
            var ldapBaseDnSetting                               = appSettings.Settings["ldap-base-dn"]?.Value;
            var multifactorApiKeySetting                        = appSettings.Settings["multifactor-nas-identifier"]?.Value;
            var multifactorApiSecretSetting                     = appSettings.Settings["multifactor-shared-secret"]?.Value;
            var serviceAccountsSetting                          = appSettings.Settings["ldap-service-accounts"]?.Value;
            var serviceAccountsOrganizationUnitSetting          = appSettings.Settings["ldap-service-accounts-ou"]?.Value;
            var activeDirectoryGroupSetting                     = appSettings.Settings["active-directory-group"]?.Value;
            var activeDirectory2FaGroupSetting                  = appSettings.Settings["active-directory-2fa-group"]?.Value;
            var activeDirectory2FaBypassGroupSetting            = appSettings.Settings["active-directory-2fa-bypass-group"]?.Value;
            var bypassSecondFactorWhenApiUnreachableSetting     = appSettings.Settings["bypass-second-factor-when-api-unreachable"]?.Value;
            var loadActiveDirectoryNestedGroupsSettings         = appSettings.Settings["load-active-directory-nested-groups"]?.Value;


            if (string.IsNullOrEmpty(ldapServerSetting))
            {
                throw new Exception("Configuration error: 'ldap-server' element not found");
            }
            if (string.IsNullOrEmpty(multifactorApiKeySetting))
            {
                throw new Exception("Configuration error: 'multifactor-nas-identifier' element not found");
            }
            if (string.IsNullOrEmpty(multifactorApiSecretSetting))
            {
                throw new Exception("Configuration error: 'multifactor-shared-secret' element not found");
            }

            var configuration = new ClientConfiguration
            {
                Name = name,
                LdapServer = ldapServerSetting,
                MultifactorApiKey = multifactorApiKeySetting,
                MultifactorApiSecret = multifactorApiSecretSetting,
                LdapBaseDn = ldapBaseDnSetting,
            };

            if (!string.IsNullOrEmpty(serviceAccountsSetting))
            {
                configuration.ServiceAccounts = serviceAccountsSetting
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(acc => acc.Trim().ToLower())
                    .ToArray();
            }

            if (!string.IsNullOrEmpty(serviceAccountsOrganizationUnitSetting))
            {
                configuration.ServiceAccountsOrganizationUnit = serviceAccountsOrganizationUnitSetting
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(acc => acc.Trim().ToLower())
                    .ToArray();
            }

            if (!string.IsNullOrEmpty(activeDirectoryGroupSetting))
            {
                configuration.ActiveDirectoryGroup = activeDirectoryGroupSetting.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            }

            if (!string.IsNullOrEmpty(activeDirectory2FaGroupSetting))
            {
                configuration.ActiveDirectory2FaGroup = activeDirectory2FaGroupSetting.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            }

            if (!string.IsNullOrEmpty(activeDirectory2FaBypassGroupSetting))
            {
                configuration.ActiveDirectory2FaBypassGroup = activeDirectory2FaBypassGroupSetting.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            }

            if (!string.IsNullOrEmpty(bypassSecondFactorWhenApiUnreachableSetting))
            {
                if (!bool.TryParse(bypassSecondFactorWhenApiUnreachableSetting, out var bypassSecondFactorWhenApiUnreachable))
                {
                    throw new Exception("Configuration error: Can't parse 'bypass-second-factor-when-api-unreachable' value");
                }

                configuration.BypassSecondFactorWhenApiUnreachable = bypassSecondFactorWhenApiUnreachable;
            }

            if (!string.IsNullOrEmpty(loadActiveDirectoryNestedGroupsSettings))
            {
                if (!bool.TryParse(loadActiveDirectoryNestedGroupsSettings, out var loadActiveDirectoryNestedGroups))
                {
                    throw new Exception("Configuration error: Can't parse 'load-active-directory-nested-groups' value");
                }

                configuration.LoadActiveDirectoryNestedGroups = loadActiveDirectoryNestedGroups;
            }

            if (userNameTransformRulesSection?.Members != null)
            {
                foreach (var member in userNameTransformRulesSection?.Members)
                {
                    if (member is UserNameTransformRulesElement rule)
                    {
                        configuration.UserNameTransformRules.Add(rule);
                    }
                }
            }

            try
            {
                configuration.AuthenticationCacheLifetime = AuthenticatedClientCacheConfig
                    .Create(appSettings.Settings[Constants.Configuration.AuthenticationCacheLifetime]?.Value);
            }
            catch
            {
                throw new Exception($"Configuration error: Can't parse '{Constants.Configuration.AuthenticationCacheLifetime}' value");
            }

            return configuration;
        }

        public static string GetLogFormat()
        {
            var appSettings = ConfigurationManager.AppSettings;
            return appSettings?["logging-format"];
        }
    }
}
