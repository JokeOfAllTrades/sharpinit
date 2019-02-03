﻿using NLog;
using SharpInit.Ipc;
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
            UnitRegistry.GetUnit(name).Activate();
            return true;
        }

        public bool DeactivateUnit(string name)
        {
            UnitRegistry.GetUnit(name).Deactivate();
            return true;
        }

        public bool ReloadUnit(string name)
        {
            UnitRegistry.GetUnit(name).Reload();
            return true;
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
    }
}
