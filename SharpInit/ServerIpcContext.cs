﻿using NLog;
using SharpInit.Ipc;
using SharpInit.Tasks;
using SharpInit.Units;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SharpInit
{
    class ServerIpcContext : IBaseIpcContext
    {
        Logger Log = LogManager.GetCurrentClassLogger();

        public ServerIpcContext()
        {

        }

        public bool ActivateUnit(string name)
        {
            var transaction = UnitRegistry.CreateActivationTransaction(name);
            var result = transaction.Execute();

            if (result.Type != ResultType.Success)
            {
                Log.Info($"Activation transaction failed. Result type: {result.Type}, message: {result.Message}");
                Log.Info("Transaction failed at highlighted task: ");

                var tree = transaction.GenerateTree(0, result.Task);

                foreach (var line in tree.Split('\n'))
                    Log.Info(line);
            }

            return result.Type == ResultType.Success;
        }

        public bool DeactivateUnit(string name)
        {
            var transaction = UnitRegistry.CreateDeactivationTransaction(name);
            var result = transaction.Execute();

            if (result.Type != ResultType.Success)
            {
                Log.Info($"Deactivation transaction failed. Result type: {result.Type}, message: {result.Message}");
                Log.Info("Transaction failed at highlighted task: ");

                var tree = transaction.GenerateTree(0, result.Task);

                foreach (var line in tree.Split('\n'))
                    Log.Info(line);
            }

            return result.Type == ResultType.Success;
        }

        public Dictionary<string, List<string>> GetActivationPlan(string unit)
        {
            var transaction = UnitRegistry.CreateActivationTransaction(unit);
            return transaction.Reasoning.ToDictionary(t => t.Key.UnitName, t => t.Value);
        }

        public Dictionary<string, List<string>> GetDeactivationPlan(string unit)
        {
            var transaction = UnitRegistry.CreateDeactivationTransaction(unit);
            return transaction.Reasoning.ToDictionary(t => t.Key.UnitName, t => t.Value);
        }

        public bool ReloadUnit(string name)
        {
            var transaction = UnitRegistry.GetUnit(name).GetReloadTransaction();
            var result = transaction.Execute();
            return result.Type == Tasks.ResultType.Success;
        }

        public List<string> ListUnits()
        {
            return UnitRegistry.Units.Select(u => u.Key).ToList();
        }

        public bool LoadUnitFromFile(string path)
        {
            try
            {
                UnitRegistry.AddUnitByPath(path);
                return true;
            }
            catch { return false; }
        }

        public bool ReloadUnitFile(string unit)
        {
            UnitRegistry.GetUnit(unit).ReloadUnitFile();
            return true;
        }

        public int RescanUnits()
        {
            return UnitRegistry.ScanDefaultDirectories();
        }

        public UnitInfo GetUnitInfo(string unit_name)
        {
            var unit = UnitRegistry.GetUnit(unit_name);
            var info = new UnitInfo();

            info.Name = unit.UnitName;
            info.Path = unit.File.UnitPath;
            info.Description = unit.File.Description;
            info.State = Enum.Parse<Ipc.UnitState>(unit.CurrentState.ToString());
            info.LastStateChangeTime = unit.LastStateChangeTime;
            info.ActivationTime = unit.ActivationTime;
            info.LoadTime = unit.LoadTime;

            return info;
        }
    }
}
