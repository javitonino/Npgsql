// created on 29/11/2007

// Npgsql.NpgsqlConnectionStringBuilder.cs
//
// Author:
//    Glen Parker (glenebob@gmail.com)
//    Ben Sagal (bensagal@gmail.com)
//    Tao Wang (dancefire@gmail.com)
//
//    Copyright (C) 2007 The Npgsql Development Team
//    npgsql-general@gborg.postgresql.org
//    http://gborg.postgresql.org/project/npgsql/projdisplay.php
//
// Permission to use, copy, modify, and distribute this software and its
// documentation for any purpose, without fee, and without a written
// agreement is hereby granted, provided that the above copyright notice
// and this paragraph and the following two paragraphs appear in all copies.
// 
// IN NO EVENT SHALL THE NPGSQL DEVELOPMENT TEAM BE LIABLE TO ANY PARTY
// FOR DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES,
// INCLUDING LOST PROFITS, ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS
// DOCUMENTATION, EVEN IF THE NPGSQL DEVELOPMENT TEAM HAS BEEN ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
// 
// THE NPGSQL DEVELOPMENT TEAM SPECIFICALLY DISCLAIMS ANY WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY
// AND FITNESS FOR A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS
// ON AN "AS IS" BASIS, AND THE NPGSQL DEVELOPMENT TEAM HAS NO OBLIGATIONS
// TO PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.DirectoryServices;
using System.Reflection;
using System.Resources;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

// Keep the xml comment warning quiet for this file.
#pragma warning disable 1591

namespace Npgsql
{
    public sealed class NpgsqlConnectionStringBuilder : DbConnectionStringBuilder
    {
        private delegate string ValueNativeToString(object value);

        private class ValueDescription
        {
            internal readonly IComparable ImplicitDefault;
            internal readonly IComparable ExplicitDefault;
            internal readonly bool DefaultsDiffer;
            internal readonly bool StoreInBase;
            private readonly ValueNativeToString NativeToString;

            /// <summary>
            /// Set both ImplicitDefault and ExplicitDefault to the <paramref name="t"/>'s default value.
            /// </summary>
            /// <param name="t"></param>
            /// <param name="storeInBase"></param>
            /// <param name="nativeToString"></param>
            internal ValueDescription(Type t, bool storeInBase = true, ValueNativeToString nativeToString = null)
            {
                ImplicitDefault = GetImplicitDefault(t);
                ExplicitDefault = ImplicitDefault;
                DefaultsDiffer = false;
                StoreInBase = storeInBase;
                NativeToString = nativeToString;
            }

            /// <summary>
            /// Set ImplicitDefault to the default value of <paramref name="explicitDefault"/>'s type,
            /// and ExplicitDefault to <paramref name="explicitDefault"/>.
            /// </summary>
            /// <param name="explicitDefault"></param>
            /// <param name="storeInBase"></param>
            /// <param name="nativeToString"></param>
            internal ValueDescription(IComparable explicitDefault, bool storeInBase = true, ValueNativeToString nativeToString = null)
            {
                ImplicitDefault = GetImplicitDefault(explicitDefault.GetType());
                ExplicitDefault = explicitDefault;
                DefaultsDiffer = (ImplicitDefault.CompareTo(ExplicitDefault) != 0);
                StoreInBase = storeInBase;
                NativeToString = nativeToString;
            }

            private static IComparable GetImplicitDefault(Type t)
            {
                if (t == typeof(string))
                {
                    return string.Empty;
                }
                else
                {
                    return (IComparable)Activator.CreateInstance(t);
                }
            }

            internal string ConvertNativeToString(object value)
            {
                string asString = value as string;

                if (asString != null)
                {
                    return asString.Trim();
                }
                else if (NativeToString != null)
                {
                    return NativeToString(value);
                }
                else
                {
                    return value.ToString();
                }
            }
        }

        private static readonly ResourceManager resman = new ResourceManager(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly Dictionary<Keywords, ValueDescription> valueDescriptions = new Dictionary<Keywords, ValueDescription>();

        private string originalConnectionString;

        private const int POOL_SIZE_LIMIT = 1024;
        private const int TIMEOUT_LIMIT = 1024;

        #region Constructors

        static NpgsqlConnectionStringBuilder()
        {
            // Set up value descriptions.
            // All connection string values have an implicit default (its type's default value),
            // and an explicit default which can be different from its
            // implicit default.
            valueDescriptions.Add(Keywords.Host, new ValueDescription(typeof(string)));
            valueDescriptions.Add(Keywords.Port, new ValueDescription((Int32)5432));
#pragma warning disable 618
            valueDescriptions.Add(Keywords.Protocol, new ValueDescription(ProtocolVersion.Version3, false));
#pragma warning restore 618
            valueDescriptions.Add(Keywords.Database, new ValueDescription(typeof(string)));
            valueDescriptions.Add(Keywords.UserName, new ValueDescription(typeof(string)));
            valueDescriptions.Add(Keywords.Password, new ValueDescription(typeof(string)));
            valueDescriptions.Add(Keywords.SSL, new ValueDescription(typeof(bool)));
            valueDescriptions.Add(Keywords.SslMode, new ValueDescription(typeof(SslMode)));
#pragma warning disable 618
            valueDescriptions.Add(Keywords.Encoding, new ValueDescription("UTF8", false));
#pragma warning restore 618
            valueDescriptions.Add(Keywords.Timeout, new ValueDescription((Int32)15));
            valueDescriptions.Add(Keywords.SearchPath, new ValueDescription(typeof(string)));
            valueDescriptions.Add(Keywords.Pooling, new ValueDescription(true));
            valueDescriptions.Add(Keywords.ConnectionLifeTime, new ValueDescription(typeof(Int32)));
            valueDescriptions.Add(Keywords.MinPoolSize, new ValueDescription((Int32)1));
            valueDescriptions.Add(Keywords.MaxPoolSize, new ValueDescription((Int32)20));
            valueDescriptions.Add(Keywords.SyncNotification, new ValueDescription(typeof(bool)));
            valueDescriptions.Add(Keywords.CommandTimeout, new ValueDescription((Int32)20));
            valueDescriptions.Add(Keywords.Enlist, new ValueDescription(typeof(bool)));
            valueDescriptions.Add(Keywords.PreloadReader, new ValueDescription(typeof(bool)));
            valueDescriptions.Add(Keywords.UseExtendedTypes, new ValueDescription(typeof(bool)));
            valueDescriptions.Add(Keywords.IntegratedSecurity, new ValueDescription(typeof(bool)));
            valueDescriptions.Add(Keywords.IncludeRealm, new ValueDescription(typeof(bool)));
            valueDescriptions.Add(Keywords.Compatible, new ValueDescription(THIS_VERSION));
            valueDescriptions.Add(Keywords.ApplicationName, new ValueDescription(typeof(string)));
            valueDescriptions.Add(Keywords.AlwaysPrepare, new ValueDescription(typeof(bool)));
        }

        public NpgsqlConnectionStringBuilder()
        {
            _password = new PasswordBytes();
            this.Clear();
        }

        public NpgsqlConnectionStringBuilder(string connectionString)
        {
            _password = new PasswordBytes();
            this.originalConnectionString = connectionString;
            base.ConnectionString = connectionString;
            CheckValues();
        }

        #endregion

        /// <summary>
        /// Return an exact copy of this NpgsqlConnectionString.
        /// </summary>
        public NpgsqlConnectionStringBuilder Clone()
        {
            NpgsqlConnectionStringBuilder builder = new NpgsqlConnectionStringBuilder();

            foreach (string key in this.Keys)
            {
                builder[key] = this[key];
            }

            return builder;
        }

        private void CheckValues()
        {
            if ((MaxPoolSize > 0) && (MinPoolSize > MaxPoolSize))
            {
                string key = GetKeyName(Keywords.MinPoolSize);
                throw new ArgumentOutOfRangeException(
                    key, String.Format(resman.GetString("Exception_IntegerKeyValMax"), key, MaxPoolSize));
            }
        }

        #region Parsing Functions

        private static SslMode ToSslMode(object value)
        {
            if (value is SslMode)
            {
                return (SslMode) value;
            }
            else
            {
                return (SslMode) Enum.Parse(typeof (SslMode), value.ToString(), true);
            }
        }

        private static int ToInt32(object value, int min, int max, Keywords keyword)
        {
            int v = Convert.ToInt32(value);

            if (v < min)
            {
                string key = GetKeyName(keyword);

                throw new ArgumentOutOfRangeException(
                    key, String.Format(resman.GetString("Exception_IntegerKeyValMin"), key, min));
            }
            else if (v > max)
            {
                string key = GetKeyName(keyword);

                throw new ArgumentOutOfRangeException(
                    key, String.Format(resman.GetString("Exception_IntegerKeyValMax"), key, max));
            }

            return v;
        }

        private static Boolean ToBoolean(object value)
        {
            string text = value as string;

            if (text != null)
            {
                switch (text.ToLowerInvariant())
                {
                    case "t":
                    case "true":
                    case "y":
                    case "yes":
                        return true;
                    case "f":
                    case "false":
                    case "n":
                    case "no":
                        return false;
                    default:
                        throw new InvalidCastException(value.ToString());
                }
            }
            else
            {
                return Convert.ToBoolean(value);
            }
        }

        private Boolean ToIntegratedSecurity(object value)
        {
            string text = value as string;
            if (text != null)
            {
                switch (text.ToLowerInvariant())
                {
                    case "t":
                    case "true":
                    case "y":
                    case "yes":
                    case "sspi":
                        return true;

                    case "f":
                    case "false":
                    case "n":
                    case "no":
                        return false;

                    default:
                        throw new InvalidCastException(value.ToString());
                }
            }
            else
            {
                return Convert.ToBoolean(value);
            }
        }

        #endregion
        #region Properties

        private string _host;
        /// <summary>
        /// Gets or sets the backend server host name.
        /// </summary>
        public string Host
        {
            get { return _host; }
            set { SetValue(GetKeyName(Keywords.Host), Keywords.Host, value); }
        }

        private int _port;
        /// <summary>
        /// Gets or sets the backend server port.
        /// </summary>
        public int Port
        {
            get { return _port; }
            set { SetValue(GetKeyName(Keywords.Port), Keywords.Port, value); }
        }

        private string _database;
        ///<summary>
        /// Gets or sets the name of the database to be used after a connection is opened.
        /// </summary>
        /// <value>The name of the database to be
        /// used after a connection is opened.</value>
        public string Database
        {
            get { return _database; }
            set { SetValue(GetKeyName(Keywords.Database), Keywords.Database, value); }
        }

    #region Integrated security
        class CachedUpn {
            public string Upn;
            public DateTime ExpiryTimeUtc;
        }

        static Dictionary<SecurityIdentifier, CachedUpn> cachedUpns = new Dictionary<SecurityIdentifier,CachedUpn>();

        private string GetIntegratedUserName()
        {
            // Side note: This maintains the hack fix mentioned before for https://github.com/npgsql/Npgsql/issues/133.
            // In a nutshell, starting with .NET 4.5 WindowsIdentity inherits from ClaimsIdentity
            // which doesn't exist in mono, and calling a WindowsIdentity method bombs.
            // The workaround is that this function that actually deals with WindowsIdentity never
            // gets called on mono, so never gets JITted and the problem goes away.

            // Gets the current user's username for integrated security purposes
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            CachedUpn cachedUpn = null;
            string upn = null;

            // Check to see if we already have this UPN cached
            lock (cachedUpns)
            {
                if (cachedUpns.TryGetValue(identity.User, out cachedUpn))
                {
                    if (cachedUpn.ExpiryTimeUtc > DateTime.UtcNow)
                        upn = cachedUpn.Upn;
                    else
                        cachedUpns.Remove(identity.User);
                }
            }

            try
            {
                if (upn == null) {
                    // Try to get the user's UPN in its correct case; this is what the
                    // server will need to verify against a Kerberos/SSPI ticket

                    // First, find a domain server we can talk to
                    string domainHostName;

                    using (DirectoryEntry rootDse = new DirectoryEntry("LDAP://rootDSE") { AuthenticationType = AuthenticationTypes.Secure })
                    {
                        domainHostName = (string) rootDse.Properties["dnsHostName"].Value;
                    }

                    // Query the domain server by the current user's SID
                    using (DirectoryEntry entry = new DirectoryEntry("LDAP://" + domainHostName) { AuthenticationType = AuthenticationTypes.Secure })
                    {
                        DirectorySearcher search = new DirectorySearcher(entry,
                            "(objectSid=" + identity.User.Value + ")", new string[] { "userPrincipalName" });

                        SearchResult result = search.FindOne();

                        upn = (string) result.Properties["userPrincipalName"][0];
                    }
                }

                if (cachedUpn == null)
                {
                    // Save this value
                    cachedUpn = new CachedUpn() { Upn = upn, ExpiryTimeUtc = DateTime.UtcNow.AddHours( 3.0 ) };

                    lock (cachedUpns)
                    {
                        cachedUpns[identity.User] = cachedUpn;
                    }
                }

                string[] upnParts = upn.Split('@');

                if(_includeRealm)
                {
                    // Make it Kerberos-y by uppercasing the realm part
                    return upnParts[0] + "@" + upnParts[1].ToUpperInvariant();
                }
                else
                {
                    return upnParts[0];
                }
            }
            catch
            {
                // Querying the directory failed, so return the SAM name
                // (which probably won't work, but it's better than nothing)
                return identity.Name.Split('\\')[1];
            }
        }
    #endregion

        private string _username;
        /// <summary>
        /// Gets or sets the login user name.
        /// </summary>
        public string UserName
        {
            get
            {
                if ((_integrated_security) && (String.IsNullOrEmpty(_username)))
                {
                    _username = GetIntegratedUserName();
                }

                return _username;
            }

            set { SetValue(GetKeyName(Keywords.UserName), Keywords.UserName, value); }
        }

        private PasswordBytes _password;
        /// <summary>
        /// Gets or sets the login password as a UTF8 encoded byte array.
        /// </summary>
        public byte[] PasswordAsByteArray
        {
            get { return _password.PasswordAsByteArray; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException("PasswordAsByteArray");
                }

                _password.PasswordAsByteArray = value;
            }
        }
        /// <summary>
        /// Sets the login password as a string.
        /// </summary>
        public string Password
        {
            set { SetValue(GetKeyName(Keywords.Password), Keywords.Password, value); }
        }

        private bool _ssl;
        /// <summary>
        /// Gets or sets a value indicating whether to attempt to use SSL.
        /// </summary>
        public bool SSL
        {
            get { return _ssl; }
            set { SetValue(GetKeyName(Keywords.SSL), Keywords.SSL, value); }
        }

        private SslMode _sslmode;
        /// <summary>
        /// Gets or sets a value indicating whether to attempt to use SSL.
        /// </summary>
        public SslMode SslMode
        {
            get { return _sslmode; }
            set { SetValue(GetKeyName(Keywords.SslMode), Keywords.SslMode, value); }
        }

        private int _timeout;
        /// <summary>
        /// Gets or sets the time to wait while trying to establish a connection
        /// before terminating the attempt and generating an error.
        /// </summary>
        /// <value>The time (in seconds) to wait for a connection to open. The default value is 15 seconds.</value>
        public int Timeout
        {
            get { return _timeout; }
            set { SetValue(GetKeyName(Keywords.Timeout), Keywords.Timeout, value); }
        }

        private string _searchpath;
        /// <summary>
        /// Gets or sets the schema search path.
        /// </summary>
        public string SearchPath
        {
            get { return _searchpath; }
            set { SetValue(GetKeyName(Keywords.SearchPath), Keywords.SearchPath, value); }
        }

        private bool _pooling;
        /// <summary>
        /// Gets or sets a value indicating whether connection pooling should be used.
        /// </summary>
        public bool Pooling
        {
            get { return _pooling; }
            set { SetValue(GetKeyName(Keywords.Pooling), Keywords.Pooling, value); }
        }

        private int _connection_life_time;
        /// <summary>
        /// Gets or sets the time to wait before closing unused connections in the pool if the count
        /// of all connections exeeds MinPoolSize.
        /// </summary>
        /// <remarks>
        /// If connection pool contains unused connections for ConnectionLifeTime seconds,
        /// the half of them will be closed. If there will be unused connections in a second
        /// later then again the half of them will be closed and so on.
        /// This strategy provide smooth change of connection count in the pool.
        /// </remarks>
        /// <value>The time (in seconds) to wait. The default value is 15 seconds.</value>
        public int ConnectionLifeTime
        {
            get { return _connection_life_time; }
            set { SetValue(GetKeyName(Keywords.ConnectionLifeTime), Keywords.ConnectionLifeTime, value); }
        }

        private int _min_pool_size;
        /// <summary>
        /// Gets or sets the minimum connection pool size.
        /// </summary>
        public int MinPoolSize
        {
            get { return _min_pool_size; }
            set { SetValue(GetKeyName(Keywords.MinPoolSize), Keywords.MinPoolSize, value); }
        }

        private int _max_pool_size;
        /// <summary>
        /// Gets or sets the maximum connection pool size.
        /// </summary>
        public int MaxPoolSize
        {
            get { return _max_pool_size; }
            set { SetValue(GetKeyName(Keywords.MaxPoolSize), Keywords.MaxPoolSize, value); }
        }

        private bool _sync_notification;
        /// <summary>
        /// Gets or sets a value indicating whether to listen for notifications and report them between command activity.
        /// </summary>
        public bool SyncNotification
        {
            get { return _sync_notification; }
            set { SetValue(GetKeyName(Keywords.SyncNotification), Keywords.SyncNotification, value); }
        }

        private int _command_timeout;
        /// <summary>
        /// Gets the time to wait while trying to execute a command
        /// before terminating the attempt and generating an error.
        /// </summary>
        /// <value>The time (in seconds) to wait for a command to complete. The default value is 20 seconds.</value>
        public int CommandTimeout
        {
            get { return _command_timeout; }
            set { SetValue(GetKeyName(Keywords.CommandTimeout), Keywords.CommandTimeout, value); }
        }

        private bool _enlist;
        public bool Enlist
        {
            get { return _enlist; }
            set { SetValue(GetKeyName(Keywords.Enlist), Keywords.Enlist, value); }
        }

        private bool _preloadReader;
        /// <summary>
        /// Gets or sets a value indicating whether datareaders are loaded in their entirety (for compatibility with earlier code).
        /// </summary>
        public bool PreloadReader
        {
            get { return _preloadReader; }
            set { SetValue(GetKeyName(Keywords.PreloadReader), Keywords.PreloadReader, value); }
        }

        private bool _useExtendedTypes;
        public bool UseExtendedTypes
        {
            get { return _useExtendedTypes; }
            set { SetValue(GetKeyName(Keywords.UseExtendedTypes), Keywords.UseExtendedTypes, value); }
        }

        private bool _integrated_security;
        public bool IntegratedSecurity
        {
            get { return _integrated_security; }
            set
            {
                if (value == true)
                    CheckIntegratedSecuritySupport();
                SetValue(GetKeyName(Keywords.IntegratedSecurity), Keywords.IntegratedSecurity, value);
            }
        }

        /// <summary>
        /// No integrated security if we're on mono and .NET 4.5 because of ClaimsIdentity,
        /// see https://github.com/npgsql/Npgsql/issues/133
        /// </summary>
        [Conditional("NET45")]
        private static void CheckIntegratedSecuritySupport()
        {
            if (Type.GetType("Mono.Runtime") != null)
                throw new NotSupportedException("IntegratedSecurity is currently unsupported on mono and .NET 4.5 (see https://github.com/npgsql/Npgsql/issues/133)");
        }

        private bool _includeRealm;
        public bool IncludeRealm
        {
            get { return _includeRealm; }
            set { SetValue(GetKeyName(Keywords.IncludeRealm), Keywords.IncludeRealm, value); }
        }

        private Version _compatible;

        private static readonly Version THIS_VERSION =
            MethodBase.GetCurrentMethod().DeclaringType.Assembly.GetName().Version;

        /// <summary>
        /// Compatibilty version. When possible, behaviour caused by breaking changes will be preserved
        /// if this version is less than that where the breaking change was introduced.
        /// </summary>
        public Version Compatible
        {
            get { return _compatible; }
            set { SetValue(GetKeyName(Keywords.Compatible), Keywords.Compatible, value); }
        }

        private string _application_name;
        /// <summary>
        /// Gets or sets the ootional application name parameter to be sent to the backend during connection initiation.
        /// </summary>
        public string ApplicationName
        {
            get { return _application_name; }
            set { SetValue(GetKeyName(Keywords.ApplicationName), Keywords.ApplicationName, value); }
        }

        private bool _always_prepare;
        /// <summary>
        /// Gets or sets a value indicating whether to silently Prepare() all commands before execution.
        /// </summary>
        public bool AlwaysPrepare
        {
            get { return _always_prepare; }
            set { SetValue(GetKeyName(Keywords.AlwaysPrepare), Keywords.AlwaysPrepare, value); }
        }

        #endregion
        #region DeprecatedProperties

        /// <summary>
        /// Gets or sets the specified backend communication protocol version.
        /// </summary>
        [Obsolete("Protocol versio 3 is always used regardless of this setting.")]
        public ProtocolVersion Protocol
        {
#pragma warning disable 618
            get { return (ProtocolVersion)valueDescriptions[Keywords.Protocol].ExplicitDefault; }
#pragma warning restore 618
        }

        /// <summary>
        /// Gets the backend encoding.  Always returns "UTF8".
        /// </summary>
        [Obsolete("UTF8 is always used regardless of this setting.")]
        public string Encoding
        {
#pragma warning disable 618
            get { return (string)valueDescriptions[Keywords.Encoding].ExplicitDefault; }
#pragma warning restore 618
        }

        #endregion

        private static Keywords GetKey(string key)
        {
            switch (key.ToUpperInvariant())
            {
                case "HOST":
                case "SERVER":
                    return Keywords.Host;
                case "PORT":
                    return Keywords.Port;
                case "PROTOCOL":
#pragma warning disable 618
                    return Keywords.Protocol;
#pragma warning restore 618
                case "DATABASE":
                case "DB":
                    return Keywords.Database;
                case "USERNAME":
                case "USER NAME":
                case "USER":
                case "USERID":
                case "USER ID":
                case "UID":
                    return Keywords.UserName;
                case "PASSWORD":
                case "PSW":
                case "PWD":
                    return Keywords.Password;
                case "SSL":
                    return Keywords.SSL;
                case "SSLMODE":
                    return Keywords.SslMode;
                case "ENCODING":
#pragma warning disable 618
                    return Keywords.Encoding;
#pragma warning restore 618
                case "TIMEOUT":
                    return Keywords.Timeout;
                case "SEARCHPATH":
                    return Keywords.SearchPath;
                case "POOLING":
                    return Keywords.Pooling;
                case "CONNECTIONLIFETIME":
                    return Keywords.ConnectionLifeTime;
                case "MINPOOLSIZE":
                    return Keywords.MinPoolSize;
                case "MAXPOOLSIZE":
                    return Keywords.MaxPoolSize;
                case "SYNCNOTIFICATION":
                    return Keywords.SyncNotification;
                case "COMMANDTIMEOUT":
                    return Keywords.CommandTimeout;
                case "ENLIST":
                    return Keywords.Enlist;
                case "PRELOADREADER":
                case "PRELOAD READER":
                    return Keywords.PreloadReader;
                case "USEEXTENDEDTYPES":
                case "USE EXTENDED TYPES":
                    return Keywords.UseExtendedTypes;
                case "INTEGRATED SECURITY":
                    return Keywords.IntegratedSecurity;
                case "INCLUDEREALM":
                    return Keywords.IncludeRealm;
                case "COMPATIBLE":
                    return Keywords.Compatible;
                case "APPLICATIONNAME":
                    return Keywords.ApplicationName;
                case "ALWAYSPREPARE":
                    return Keywords.AlwaysPrepare;
                default:
                    throw new ArgumentException(resman.GetString("Exception_WrongKeyVal"), key);
            }
        }

        internal static string GetKeyName(Keywords keyword)
        {
            switch (keyword)
            {
                case Keywords.Host:
                    return "HOST";
                case Keywords.Port:
                    return "PORT";
#pragma warning disable 618
                case Keywords.Protocol:
#pragma warning restore 618
                    return "PROTOCOL";
                case Keywords.Database:
                    return "DATABASE";
                case Keywords.UserName:
                    return "USER ID";
                case Keywords.Password:
                    return "PASSWORD";
                case Keywords.SSL:
                    return "SSL";
                case Keywords.SslMode:
                    return "SSLMODE";
#pragma warning disable 618
                case Keywords.Encoding:
#pragma warning restore 618
                    return "ENCODING";
                case Keywords.Timeout:
                    return "TIMEOUT";
                case Keywords.SearchPath:
                    return "SEARCHPATH";
                case Keywords.Pooling:
                    return "POOLING";
                case Keywords.ConnectionLifeTime:
                    return "CONNECTIONLIFETIME";
                case Keywords.MinPoolSize:
                    return "MINPOOLSIZE";
                case Keywords.MaxPoolSize:
                    return "MAXPOOLSIZE";
                case Keywords.SyncNotification:
                    return "SYNCNOTIFICATION";
                case Keywords.CommandTimeout:
                    return "COMMANDTIMEOUT";
                case Keywords.Enlist:
                    return "ENLIST";
                case Keywords.PreloadReader:
                    return "PRELOADREADER";
                case Keywords.UseExtendedTypes:
                    return "USEEXTENDEDTYPES";
                case Keywords.IntegratedSecurity:
                    return "INTEGRATED SECURITY";
                case Keywords.IncludeRealm:
                    return "INCLUDEREALM";
                case Keywords.Compatible:
                    return "COMPATIBLE";
                case Keywords.ApplicationName:
                    return "APPLICATIONNAME";
                case Keywords.AlwaysPrepare:
                    return "ALWAYSPREPARE";
                default:
                    return keyword.ToString().ToUpperInvariant();
            }
        }

        internal static object GetDefaultValue(Keywords keyword)
        {
            return valueDescriptions[keyword].ExplicitDefault;
        }

        /// <summary>
        /// Case insensative accessor for indivual connection string values.
        /// </summary>
        public override object this[string keyword]
        {
            get { return GetValue(GetKey(keyword)); }
            set { this[GetKey(keyword)] = value; }
        }

        public object this[Keywords keyword]
        {
            get { return GetValue(keyword); }
            set { SetValue(GetKeyName(keyword), keyword, value); }
        }

        public override bool Remove(string keyword)
        {
            Keywords key = GetKey(keyword);
            SetValue(key, GetDefaultValue(key));
            return base.Remove(keyword);
        }

        public bool ContainsKey(Keywords keyword)
        {
            return base.ContainsKey(GetKeyName(keyword));
        }

        /// <summary>
        /// This function will set value for known key, both private member and base[key].
        /// </summary>
        /// <param name="keyword"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns>value, coerced as needed to the stored type.</returns>
        private object SetValue(string keyword, Keywords key, object value)
        {
            ValueDescription description;

            description = valueDescriptions[key];

            if (! description.StoreInBase)
            {
                return value;
            }

            if (value == null)
            {
                Remove(keyword);
                return value;
            }

            value = SetValue(key, value);

            // If the value matches both the parameter's default and the type's default, remove it from base,
            // otherwise convert to string and set the base value.
            if (
                description.DefaultsDiffer ||
                (description.ExplicitDefault.CompareTo((IComparable)value) != 0)
            )
            {
                base[keyword] = description.ConvertNativeToString(value);
            }
            else
            {
                base.Remove(keyword);
            }

            return value;
        }

        /// <summary>
        /// The function will modify private member only, not base[key].
        /// </summary>
        /// <param name="keyword"></param>
        /// <param name="value"></param>
        /// <returns>value, coerced as needed to the stored type.</returns>
        private object SetValue(Keywords keyword, object value)
        {
            try
            {
                switch (keyword)
                {
                    case Keywords.Host:
                        return this._host = Convert.ToString(value);
                    case Keywords.Port:
                        return this._port = Convert.ToInt32(value);
#pragma warning disable 618
                    case Keywords.Protocol:
                        return Protocol;
#pragma warning restore 618
                    case Keywords.Database:
                        return this._database = Convert.ToString(value);
                    case Keywords.UserName:
                        return this._username = Convert.ToString(value);
                    case Keywords.Password:
                        this._password.Password = value as string;
                        return value as string;
                    case Keywords.SSL:
                        return this._ssl = ToBoolean(value);
                    case Keywords.SslMode:
                        return this._sslmode = ToSslMode(value);
#pragma warning disable 618
                    case Keywords.Encoding:
                        return Encoding;
#pragma warning restore 618
                    case Keywords.Timeout:
                        return this._timeout = ToInt32(value, 0, TIMEOUT_LIMIT, keyword);
                    case Keywords.SearchPath:
                        return this._searchpath = Convert.ToString(value);
                    case Keywords.Pooling:
                        return this._pooling = ToBoolean(value);
                    case Keywords.ConnectionLifeTime:
                        return this._connection_life_time = Convert.ToInt32(value);
                    case Keywords.MinPoolSize:
                        return this._min_pool_size = ToInt32(value, 0, POOL_SIZE_LIMIT, keyword);
                    case Keywords.MaxPoolSize:
                        return this._max_pool_size = ToInt32(value, 0, POOL_SIZE_LIMIT, keyword);
                    case Keywords.SyncNotification:
                        return this._sync_notification = ToBoolean(value);
                    case Keywords.CommandTimeout:
                        return this._command_timeout = Convert.ToInt32(value);
                    case Keywords.Enlist:
                        return this._enlist = ToBoolean(value);
                    case Keywords.PreloadReader:
                        return this._preloadReader = ToBoolean(value);
                    case Keywords.UseExtendedTypes:
                        return this._useExtendedTypes = ToBoolean(value);
                    case Keywords.IntegratedSecurity:
                        bool iS = ToIntegratedSecurity(value);
                        if (iS == true)
                        {
                            CheckIntegratedSecuritySupport();
                        }

                        return this._integrated_security = ToIntegratedSecurity(iS);

                    case Keywords.IncludeRealm:
                        return this._includeRealm = ToBoolean(value);
                    case Keywords.Compatible:
                        Version ver = new Version(value.ToString());
                        if (ver > THIS_VERSION)
                            throw new ArgumentException("Attempt to set compatibility with version " + value +
                                                        " when using version " + THIS_VERSION);
                        return _compatible = ver;
                    case Keywords.ApplicationName:
                        return this._application_name = Convert.ToString(value);
                    case Keywords.AlwaysPrepare:
                        return this._always_prepare = Convert.ToBoolean(value);
                }
            }
            catch (InvalidCastException exception)
            {
                string exception_template = string.Empty;

                switch (keyword)
                {
                    case Keywords.Port:
                    case Keywords.Timeout:
                    case Keywords.ConnectionLifeTime:
                    case Keywords.MinPoolSize:
                    case Keywords.MaxPoolSize:
                    case Keywords.CommandTimeout:
                        exception_template = resman.GetString("Exception_InvalidIntegerKeyVal");
                        break;
                    case Keywords.SSL:
                    case Keywords.Pooling:
                    case Keywords.SyncNotification:
                        exception_template = resman.GetString("Exception_InvalidBooleanKeyVal");
                        break;
#pragma warning disable 618
                    case Keywords.Protocol:
#pragma warning restore 618
                        exception_template = resman.GetString("Exception_InvalidProtocolVersionKeyVal");
                        break;
                }

                if (!string.IsNullOrEmpty(exception_template))
                {
                    string key_name = GetKeyName(keyword);

                    throw new ArgumentException(string.Format(exception_template, key_name), key_name, exception);
                }

                throw;
            }

            return null;
        }

        /// <summary>
        /// The function will access private member only, not base[key].
        /// </summary>
        /// <param name="keyword"></param>
        /// <returns>value.</returns>
        private object GetValue(Keywords keyword)
        {
            switch (keyword)
            {
                case Keywords.Host:
                    return this._host;
                case Keywords.Port:
                    return this._port;
#pragma warning disable 618
                case Keywords.Protocol:
                    return Protocol;
#pragma warning restore 618
                case Keywords.Database:
                    return this._database;
                case Keywords.UserName:
                    return this._username;
                case Keywords.Password:
                    return this._password.Password;
                case Keywords.SSL:
                    return this._ssl;
                case Keywords.SslMode:
                    return this._sslmode;
#pragma warning disable 618
                case Keywords.Encoding:
                    return Encoding;
#pragma warning restore 618
                case Keywords.Timeout:
                    return this._timeout;
                case Keywords.SearchPath:
                    return this._searchpath;
                case Keywords.Pooling:
                    return this._pooling;
                case Keywords.ConnectionLifeTime:
                    return this._connection_life_time;
                case Keywords.MinPoolSize:
                    return this._min_pool_size;
                case Keywords.MaxPoolSize:
                    return this._max_pool_size;
                case Keywords.SyncNotification:
                    return this._sync_notification;
                case Keywords.CommandTimeout:
                    return this._command_timeout;
                case Keywords.Enlist:
                    return this._enlist;
                case Keywords.PreloadReader:
                    return this._preloadReader;
                case Keywords.UseExtendedTypes:
                    return this._useExtendedTypes;
                case Keywords.IntegratedSecurity:
                    return this._integrated_security;
                case Keywords.IncludeRealm:
                    return this._includeRealm;
                case Keywords.Compatible:
                    return _compatible;
                case Keywords.ApplicationName:
                    return this._application_name;
                case Keywords.AlwaysPrepare:
                    return this._always_prepare;
                default:
                    return null;

            }
        }


        /// <summary>
        /// Clear the member and assign them to the default value.
        /// </summary>
        public override void Clear()
        {
            base.Clear();

            foreach (KeyValuePair<Keywords, ValueDescription> kvp in valueDescriptions)
            {
                if (kvp.Value.StoreInBase)
                {
                    SetValue(GetKeyName(kvp.Key), kvp.Key, kvp.Value.ExplicitDefault);
                }
            }
        }

        // Store a password as a byte[].  On update, first wipe the old password value.
        private class PasswordBytes
        {
            private byte[] password = new byte[0];

            private void Wipe()
            {
                for (int i = 0 ; i < password.Length ; i++)
                {
                    password[i] = 0;
                }
            }

            internal byte[] PasswordAsByteArray
            {
                set
                {
                    Wipe();
                    password = new byte[value.Length];
                    value.CopyTo(password, 0);
                }

                get { return password; }
            }

            internal string Password
            {
                set
                {
                    Wipe();
                    password = BackendEncoding.UTF8Encoding.GetBytes(value);
                }

                get { return BackendEncoding.UTF8Encoding.GetString(password); }
            }
        }
    }

    public enum Keywords
    {
        Host,
        Port,
        [Obsolete("Protocol versio 3 is always used regardless of this setting.")]
        Protocol,
        Database,
        UserName,
        Password,
        SSL,
        SslMode,
        [Obsolete("UTF-8 is always used regardless of this setting.")]
        Encoding,
        Timeout,
        SearchPath,
        // These are for the connection pool
        Pooling,
        ConnectionLifeTime,
        MinPoolSize,
        MaxPoolSize,
        SyncNotification,
        // These are for the command
        CommandTimeout,
        // These are for the resource manager
        Enlist,
        PreloadReader,
        UseExtendedTypes,
        IntegratedSecurity,
        Compatible,
        ApplicationName,
        AlwaysPrepare,
        IncludeRealm,
    }

    public enum SslMode
    {
        Disable = 1 << 0,
        Allow = 1 << 1,
        Prefer = 1 << 2,
        Require = 1 << 3
    }
}

#pragma warning restore 1591
