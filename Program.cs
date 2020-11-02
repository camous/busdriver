using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using System.Diagnostics;

namespace busdriver
{
    class Program
    {
        static void Main(string[] args)
        {
            var config = new ConfigurationBuilder()
              .AddJsonFile("appsettings.json", false, false)
              .Build();

            // get the profile name from either command parameter or from config profile
            var profilename = args.SingleOrDefault() ?? config["profile"];

            // check & get profile existance (receivername is mandatory property)
            var profileprefix = "profiles:" + profilename + ":"; // use to navigate into app settings
            if (config[profileprefix + "receivername"] == null)
                throw new ArgumentException($"profile `{profilename}` not found in appsettings.json");
            else
            {
                JToken jAppSettings = JToken.Parse(
                    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
                )["profiles"][profilename];

                var entityPath = GetEntityPath(jAppSettings);

                var filter_body = new acmevalidator.Validator(jAppSettings["filters"]["body"] as JObject);
                var filter_userproperties = new acmevalidator.Validator(jAppSettings["filters"]["userproperties"] as JObject);
                var filter_systemproperties = new acmevalidator.Validator(jAppSettings["filters"]["systemproperties"] as JObject);

                var connectionString = config["environments:" + config[profileprefix + "environment"]];
                var messages = BrowseMessages(connectionString, entityPath);

                var output = new JArray();
                var filteredout = 0;
                foreach (var message in messages)
                {
                    var systemproperties = JObject.FromObject(message.SystemProperties);

                    // ScheduledEnqueueTimeUtc is for unknown reason not part of systemproperty, we reinject
                    systemproperties.Add("ScheduledEnqueueTimeUtc", message.ScheduledEnqueueTimeUtc);

                    var userproperties = JObject.FromObject(message.UserProperties);
                    var body = JObject.Parse(Encoding.UTF8.GetString(message.Body));

                    // use acmevalidtor nuget to filter the message based on json rules
                    if (!filter_body.Validate(body) || !filter_systemproperties.Validate(systemproperties) || !filter_userproperties.Validate(userproperties))
                    {
                        filteredout++;
                        continue;
                    }

                    output.Add(new JObject
                    {
                        ["id"] = message.MessageId,
                        ["body"] = body,
                        ["userproperties"] = userproperties,
                        ["systemproperties"] = systemproperties
                    });
                }

                // if no outputfilename, only drop to Console.Out for pipe usage
                if(jAppSettings["outputfilename"] == null)
                    Console.Out.Write(JsonConvert.SerializeObject(output, Formatting.Indented));
                else 
                    File.WriteAllText(jAppSettings["outputfilename"].Value<string>(), JsonConvert.SerializeObject(output, Formatting.Indented));
                
            }
        }

        // Generates entityPath for queue or subscription + deadletter flag
        static string GetEntityPath(JToken configuration)
        {
            Console.WriteLine(configuration);

            if (configuration["receivername"] == null)
                throw new ArgumentException("configuration property `receivername` is missing");

            var entityPath = configuration["receivername"].Value<string>();
            if (configuration["topicname"] != null)
                entityPath = EntityNameHelper.FormatSubscriptionPath(configuration["topicname"].Value<string>(), configuration["receivername"].Value<string>());

            if (configuration["deadletter"] != null && configuration["deadletter"].Value<bool>() == true)
                entityPath = EntityNameHelper.FormatDeadLetterPath(entityPath);

            return entityPath;
        }

        // browse receiver message and fetch message body
        static List<Message> BrowseMessages(string connectionString, string entityPath)
        {
            var client = new MessageReceiver(connectionString, entityPath, ReceiveMode.PeekLock);

            long lastsequence = 0;
            int maxmessages = 250;

            List<Message> messages = new List<Message>();
            IList<Message> peekedmessages = null;
            do
            {
                peekedmessages = client.PeekBySequenceNumberAsync(lastsequence, maxmessages).Result;
                messages.AddRange(peekedmessages);
                lastsequence = client.LastPeekedSequenceNumber;
            } while (peekedmessages.Count != 1);

            return messages;
        }
    }
}