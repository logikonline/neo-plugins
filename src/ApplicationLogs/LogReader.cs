// Copyright (C) 2015-2022 The Neo Project.
//
// The Neo.Plugins.ApplicationLogs is free software distributed under the MIT software license,
// see the accompanying file LICENSE in the main directory of the
// project or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Neo.IO;
using Neo.IO.Data.LevelDB;
using Neo.Json;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.VM;
using System;
using System.Collections.Generic;
using System.Linq;
using static System.IO.Path;

namespace Neo.Plugins
{
    public class LogReader : Plugin
    {
        private DB db;
        private WriteBatch _writeBatch;

        public override string Name => "ApplicationLogs";
        public override string Description => "Synchronizes the smart contract log with the NativeContract log (Notify)";

        public LogReader()
        {
            Blockchain.Committing += OnCommitting;
            Blockchain.Committed += OnCommitted;
        }

        public override void Dispose()
        {
            Blockchain.Committing -= OnCommitting;
            Blockchain.Committed -= OnCommitted;
        }

        protected override void Configure()
        {
            Settings.Load(GetConfiguration());
            string path = string.Format(Settings.Default.Path, Settings.Default.Network.ToString("X8"));
            db = DB.Open(GetFullPath(path), new Options { CreateIfMissing = true });
        }

        protected override void OnSystemLoaded(NeoSystem system)
        {
            if (system.Settings.Network != Settings.Default.Network) return;
            RpcServerPlugin.RegisterMethods(this, Settings.Default.Network);
        }

        [RpcMethod]
        public JToken GetApplicationLog(JArray _params)
        {
            UInt256 hash = UInt256.Parse(_params[0].AsString());
            byte[] value = db.Get(ReadOptions.Default, hash.ToArray());
            if (value is null)
                throw new RpcException(-100, "Unknown transaction/blockhash");

            JObject raw = (JObject)JToken.Parse(Neo.Utility.StrictUTF8.GetString(value));
            //Additional optional "trigger" parameter to getapplicationlog for clients to be able to get just one execution result for a block.
            if (_params.Count >= 2 && Enum.TryParse(_params[1].AsString(), true, out TriggerType trigger))
            {
                var executions = raw["executions"] as JArray;
                for (int i = 0; i < executions.Count;)
                {
                    if (!executions[i]["trigger"].AsString().Equals(trigger.ToString(), StringComparison.OrdinalIgnoreCase))
                        executions.RemoveAt(i);
                    else
                        i++;
                }
            }
            return raw;
        }

        public static JObject TxLogToJson(Blockchain.ApplicationExecuted appExec)
        {
            global::System.Diagnostics.Debug.Assert(appExec.Transaction != null);

            var txJson = new JObject();
            txJson["txid"] = appExec.Transaction.Hash.ToString();
            JObject trigger = new JObject();
            trigger["trigger"] = appExec.Trigger;
            trigger["vmstate"] = appExec.VMState;
            trigger["exception"] = GetExceptionMessage(appExec.Exception);
            trigger["gasconsumed"] = appExec.GasConsumed.ToString();
            try
            {
                trigger["stack"] = appExec.Stack.Select(q => q.ToJson(Settings.Default.MaxStackSize)).ToArray();
            }
            catch (Exception ex)
            {
                trigger["exception"] = ex.Message;
            }
            trigger["notifications"] = appExec.Notifications.Select(q =>
            {
                JObject notification = new JObject();
                notification["contract"] = q.ScriptHash.ToString();
                notification["eventname"] = q.EventName;
                try
                {
                    notification["state"] = q.State.ToJson();
                }
                catch (InvalidOperationException)
                {
                    notification["state"] = "error: recursive reference";
                }
                return notification;
            }).ToArray();

            txJson["executions"] = new[] { trigger };
            return txJson;
        }

        public static JObject BlockLogToJson(Block block, IReadOnlyList<Blockchain.ApplicationExecuted> applicationExecutedList)
        {
            var blocks = applicationExecutedList.Where(p => p.Transaction is null).ToArray();
            if (blocks.Length > 0)
            {
                var blockJson = new JObject();
                var blockHash = block.Hash.ToArray();
                blockJson["blockhash"] = block.Hash.ToString();
                var triggerList = new List<JObject>();
                foreach (var appExec in blocks)
                {
                    JObject trigger = new JObject();
                    trigger["trigger"] = appExec.Trigger;
                    trigger["vmstate"] = appExec.VMState;
                    trigger["gasconsumed"] = appExec.GasConsumed.ToString();
                    try
                    {
                        trigger["stack"] = appExec.Stack.Select(q => q.ToJson(Settings.Default.MaxStackSize)).ToArray();
                    }
                    catch (Exception ex)
                    {
                        trigger["exception"] = ex.Message;
                    }
                    trigger["notifications"] = appExec.Notifications.Select(q =>
                    {
                        JObject notification = new JObject();
                        notification["contract"] = q.ScriptHash.ToString();
                        notification["eventname"] = q.EventName;
                        try
                        {
                            notification["state"] = q.State.ToJson();
                        }
                        catch (InvalidOperationException)
                        {
                            notification["state"] = "error: recursive reference";
                        }
                        return notification;
                    }).ToArray();
                    triggerList.Add(trigger);
                }
                blockJson["executions"] = triggerList.ToArray();
                return blockJson;
            }

            return null;
        }

        private void OnCommitting(NeoSystem system, Block block, DataCache snapshot, IReadOnlyList<Blockchain.ApplicationExecuted> applicationExecutedList)
        {
            if (system.Settings.Network != Settings.Default.Network) return;

            ResetBatch();

            //processing log for transactions
            foreach (var appExec in applicationExecutedList.Where(p => p.Transaction != null))
            {
                var txJson = TxLogToJson(appExec);
                Put(appExec.Transaction.Hash.ToArray(), Neo.Utility.StrictUTF8.GetBytes(txJson.ToString()));
            }

            //processing log for block
            var blockJson = BlockLogToJson(block, applicationExecutedList);
            if (blockJson != null)
            {
                Put(block.Hash.ToArray(), Neo.Utility.StrictUTF8.GetBytes(blockJson.ToString()));
            }
        }

        private void OnCommitted(NeoSystem system, Block block)
        {
            if (system.Settings.Network != Settings.Default.Network) return;
            db.Write(WriteOptions.Default, _writeBatch);
        }

        static string GetExceptionMessage(Exception exception)
        {
            return exception?.GetBaseException().Message;
        }

        private void ResetBatch()
        {
            _writeBatch = new WriteBatch();
        }

        private void Put(byte[] key, byte[] value)
        {
            _writeBatch.Put(key, value);
        }
    }
}
