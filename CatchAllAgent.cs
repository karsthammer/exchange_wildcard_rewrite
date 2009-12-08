﻿// ***************************************************************
//
// CatchAllAgent
//
// An Exchange 2007 Transport Protocol agent that implements 
// catch all.
//
// HowTo:
//
// 1) Copy both the assembly and config.xml file at the same
//    location on yourt Exchange 2007 Edge or (Internet facing)
//    Hub role.
//
// 2) Add entries to the config.xml for the domains for which
//    you want catch all to be working. Entries look like this:
//    <config>
//	    <domain name="domain1.com" address="catchall@domain1.com" />
// 	    <domain name="domain2.com" address="admin@domain2.com" />
//    </config>
//    Note: The messages will be redirected to the specified 
//    'address' and it's important that those addresses really 
//    exist. Otherwise the message will NDR, or Recipient 
//    Filtering will reject the message.
//
// 3) Use the install-transportagent task to install the agent
//
// 4) Use the enable-transportagent task to enable the agent. 
//    It's important to specify a priority which is higher than
//    the priority of the Recipient Filtering agent.
//
// Features:
//
// 1. Changes to the configuration will be picked up 
//    automatically.
//
// 2. If the new configuration is invalid, it will ignore the new
//    configuration.
//
// 3. It's possible to dump traces to a file (traces.log in the 
//    example below) by adding the following to the application 
//    configuration file (edgetransport.exe.config):
//
//    <configuration>
//      <system.diagnostics>
//        <trace autoflush="false" indentsize="4">
//          <listeners>
//            <add name="myListener" 
//             type="System.Diagnostics.TextWriterTraceListener" 
//             initializeData="traces.log" />
//            <remove name="Default" />
//          </listeners>
//        </trace>
//      </system.diagnostics>
//    </configuration>
//
// ***************************************************************

namespace CatchAll
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.IO;
    using System.Xml;
    using System.Globalization;
    using System.Diagnostics;
    using System.Threading;
    using System.Text.RegularExpressions;

    using Microsoft.Exchange.Data.Transport;
    using Microsoft.Exchange.Data.Transport.Smtp;

    /// <summary>
    /// CatchAllFactory: The agent factory for the CatchAllAgent.
    /// </summary>
    public class CatchAllFactory : SmtpReceiveAgentFactory
    {
        private CatchAllConfig catchAllConfig = new CatchAllConfig();

        /// <summary>
        /// Creates a CatchAllAgent instance.
        /// </summary>
        /// <param name="server">The SMTP server.</param>
        /// <returns>An agent instance.</returns>
        public override SmtpReceiveAgent CreateAgent(SmtpServer server)
        {
            return new CatchAllAgent(catchAllConfig, server.AddressBook);
        }
    }

    /// <summary>
    /// CatchAllAgent: An SmtpReceiveAgent implementing catch-all.
    /// </summary>
    public class CatchAllAgent : SmtpReceiveAgent
    {
        /// <summary>
        /// The address book to be used for lookups.
        /// </summary>
        private AddressBook addressBook;

        /// <summary>
        /// The configuration
        /// </summary>
        private CatchAllConfig catchAllConfig;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="catchAllConfig">The configuration.</param>
        /// <param name="addressBook">The address book to perform lookups.</param>
        public CatchAllAgent(CatchAllConfig catchAllConfig, AddressBook addressBook)
        {
            // Save the address book and configuration
            this.addressBook = addressBook;
            this.catchAllConfig = catchAllConfig;

            // Register an OnRcptCommand event handler
            this.OnRcptCommand += new RcptCommandEventHandler(this.RcptToHandler);
        }

        /// <summary>
        /// Handles the "RCPT TO:" SMTP command
        /// </summary>
        /// <param name="source">The event source.</param>
        /// <param name="eodArgs">The event arguments.</param>
        public void RcptToHandler(ReceiveEventSource source, RcptCommandEventArgs rcptArgs)
        {
            RoutingAddress catchAllAddress;

            // Check whether to handle the recipient's domain
            //if (this.catchAllConfig.AddressMap.TryGetValue(
            //    rcptArgs.RecipientAddress.DomainPart.ToLower(),
            //    out catchAllAddress))
            foreach (Regex r in this.catchAllConfig.AddressMap.Keys)
            {
                if(r.IsMatch(rcptArgs.RecipientAddress.ToString()))
                {
                    // DO THE DIRTY WORK HERE
                }
            }
            if (false)
            {
                // Rewrite the (envelope) recipient address if 'not found'
                if ((this.addressBook != null) && 
                    (this.addressBook.Find(rcptArgs.RecipientAddress) == null))
                {
                    rcptArgs.RecipientAddress = catchAllAddress;
                }
            }

            return;
        }
    }

    /// <summary>
    /// CatchAllConfig: The configuration for the CatchAllAgent.
    /// </summary>
    public class CatchAllConfig
    {
        /// <summary>
        ///  The name of the configuration file.
        /// </summary>
        private static readonly string configFileName = "config.xml";

        /// <summary>
        /// Point out the directory with the configuration file (= assembly location)
        /// </summary>
        private string configDirectory;

        /// <summary>
        /// The filesystem watcher to monitor configuration file updates.
        /// </summary>
        private FileSystemWatcher configFileWatcher;

        /// <summary>
        /// The (domain to) catchall address map
        /// </summary>
        private Dictionary<Regex, RoutingAddress> addressMap;

        /// <summary>
        /// Whether reloading is ongoing
        /// </summary>
        private int reLoading = 0;

        /// <summary>
        /// Constructor.
        /// </summary>
        public CatchAllConfig()
        {
            // Setup a file system watcher to monitor the configuration file
            this.configDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            this.configFileWatcher = new FileSystemWatcher(this.configDirectory);
            this.configFileWatcher.NotifyFilter = NotifyFilters.LastWrite;
            this.configFileWatcher.Filter = "config.xml";
            this.configFileWatcher.Changed += new FileSystemEventHandler(this.OnChanged);

            // Create an initially empty map
            this.addressMap = new Dictionary<Regex, RoutingAddress>();

            // Load the configuration
            this.Load();

            // Now start monitoring
            this.configFileWatcher.EnableRaisingEvents = true;
        }

        /// <summary>
        /// The mapping between domain to catchall address.
        /// </summary>
        public Dictionary<Regex, RoutingAddress> AddressMap
        {
            get { return this.addressMap; }
        }

        /// <summary>
        /// Configuration changed handler.
        /// </summary>
        /// <param name="source">Event source.</param>
        /// <param name="e">Event arguments.</param>
        private void OnChanged(object source, FileSystemEventArgs e)
        {
            // Ignore if load ongoing
            if (Interlocked.CompareExchange(ref this.reLoading, 1, 0) != 0)
            {
                Trace.WriteLine("load ongoing: ignore");
                return;
            }

            // (Re) Load the configuration
            this.Load();

            // Reset the reload indicator
            this.reLoading = 0;
        }

        /// <summary>
        /// Load the configuration file. If any errors occur, does nothing.
        /// </summary>
        private void Load()
        {
            // Load the configuration
            XmlDocument doc = new XmlDocument();
            bool docLoaded = false;
            string fileName = Path.Combine(
                this.configDirectory,
                CatchAllConfig.configFileName);

            try
            {
                doc.Load(fileName);
                docLoaded = true;
            }
            catch (FileNotFoundException)
            {
                Trace.WriteLine("configuration file not found: {0}", fileName);
            }
            catch (XmlException e)
            {
                Trace.WriteLine("XML error: {0}", e.Message);
            }
            catch (IOException e)
            {
                Trace.WriteLine("IO error: {0}", e.Message);
            }

            // If a failure occured, ignore and simply return
            if (!docLoaded || doc.FirstChild == null)
            {
                Trace.WriteLine("configuration error: either no file or an XML error");
                return;
            }

            // Create a dictionary to hold the mappings
            Dictionary<Regex, RoutingAddress> map = new Dictionary<Regex, RoutingAddress>(100);

            // Track whether there are invalid entries
            bool invalidEntries = false;

            // Validate all entries and load into a dictionary
            foreach (XmlNode node in doc.FirstChild.ChildNodes)
            {
                if (string.Compare(node.Name, "rewriteAddress", true, CultureInfo.InvariantCulture) != 0)
                {
                    continue;
                }

                // address is the address to which the address of the incoming mail is matched.
                XmlAttribute address = node.Attributes["address"];
                // recipient is the address whereto the mail is forwarded if the match succeeds.
                XmlAttribute recipient = node.Attributes["recipient"];

                // Validate the data
                if (address == null || recipient == null)
                {
                    invalidEntries = true;
                    Trace.WriteLine("reject configuration due to an incomplete entry. (Either or both match address and recipient address missing.)");
                    break;
                }

                if (!RoutingAddress.IsValidAddress(recipient.Value))
                {
                    invalidEntries = true;
                    Trace.WriteLine(String.Format("reject configuration due to an invalid address ({0}).", address));
                    break;
                }

                // Add the new entry
                Regex lowerAddress = new Regex(address.Value.Replace("*", "(.*)+"), RegexOptions.IgnoreCase);
                map[lowerAddress] = new RoutingAddress(recipient.Value);

                Trace.WriteLine(String.Format("added entry ({0} -> {1})", lowerAddress, recipient.Value));
            }

            // If there are no invalid entries, swap in the map
            if (!invalidEntries)
            {
                Interlocked.Exchange<Dictionary<Regex, RoutingAddress>>(ref this.addressMap, map);
                Trace.WriteLine("accepted configuration");
            }
        }
    }
}
