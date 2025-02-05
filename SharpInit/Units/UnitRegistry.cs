﻿using SharpInit.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SharpInit.Units
{
    public static class UnitRegistry
    {
        public static event OnUnitStateChange UnitStateChange;

        public static Dictionary<string, Unit> Units = new Dictionary<string, Unit>();
        public static Dictionary<string, Type> UnitTypes = new Dictionary<string, Type>();

        public static DependencyGraph<OrderingDependency> OrderingDependencies = new DependencyGraph<OrderingDependency>();
        public static DependencyGraph<RequirementDependency> RequirementDependencies = new DependencyGraph<RequirementDependency>();

        public static ServiceManager ServiceManager = new ServiceManager();

        public static object GlobalTransactionLock = new object();

        public static List<string> DefaultScanDirectories = new List<string>()
        {
            "./units",
            "/etc/sharpinit/units",
            "/usr/local/sharpinit/units"
        };

        public static List<string> ScanDirectories = new List<string>();

        public static void AddUnit(Unit unit)
        {
            if (unit == null)
                return;

            if (Units.ContainsKey(unit.UnitName))
                throw new InvalidOperationException();

            unit.ServiceManager = ServiceManager;
            unit.UnitStateChange += PropagateStateChange;
            unit.RegisterDependencies(OrderingDependencies, RequirementDependencies);
            Units[unit.UnitName] = unit;
        }

        private static void PropagateStateChange(Unit source, UnitState next_state)
        {
            UnitStateChange?.Invoke(source, next_state);
        }

        public static void AddUnitByPath(string path) => AddUnit(CreateUnit(path));

        public static int ScanDefaultDirectories()
        {
            int count = 0;

            OrderingDependencies.Dependencies.Clear();
            RequirementDependencies.Dependencies.Clear();

            var env_units_parts = (Environment.GetEnvironmentVariable("SHARPINIT_UNIT_PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);

            ScanDirectories.Clear();
            ScanDirectories.AddRange(DefaultScanDirectories);
            ScanDirectories.AddRange(env_units_parts.Where(Directory.Exists));

            foreach (var unit in Units)
            {
                unit.Value.ReloadUnitFile();
                unit.Value.RegisterDependencies(OrderingDependencies, RequirementDependencies);
            }

            foreach (var dir in ScanDirectories)
            {
                if (!Directory.Exists(dir))
                    continue;

                count += ScanDirectory(dir);
            }

            return count;
        }

        public static int ScanDirectory(string path, bool recursive = false)
        {
            var directories = recursive ? Directory.GetDirectories(path) : new string[0];
            var files = Directory.GetFiles(path);

            int count = 0;

            foreach (var file in files)
            {
                var unit = CreateUnit(file);

                if (unit == null)
                {
                    continue;
                }

                if (!Units.ContainsKey(unit.UnitName))
                {
                    AddUnit(unit);
                }

                count++;
            }

            foreach (var dir in directories)
                count += ScanDirectory(dir, recursive);

            return count;
        }

        public static Unit GetUnit(string name)
        {
            if (Units.ContainsKey(name))
            {
                return Units[name];
            }

            var name_without_suffix = string.Join(".", name.Split('.').SkipLast(1));
            var suffix = "." + name.Split('.').Last();

            if (!name_without_suffix.Contains("@"))
                return null;

            var nonparametrized_name = name_without_suffix.Split('@')[0];
            var parameter = name_without_suffix.Split('@')[1];

            if(Units.ContainsKey(nonparametrized_name))
            {
                // TODO: Customize the UnitFile passed to the Unit constructor here
                var clone_unit = (Unit)Activator.CreateInstance(UnitTypes[suffix], Units[nonparametrized_name].File);
                Units[name] = clone_unit;
                return clone_unit;
            }

            return null;
        }

        public static Unit CreateUnit(string path)
        {
            var ext = Path.GetExtension(path);

            if (!UnitTypes.ContainsKey(ext))
                return null;

            return (Unit)Activator.CreateInstance(UnitTypes[ext], path);
        }

        public static void InitializeTypes()
        {
            UnitTypes[".unit"] = typeof(Unit);
            UnitTypes[".service"] = typeof(ServiceUnit);
            UnitTypes[".target"] = typeof(TargetUnit);
        }

        public static UnitStateChangeTransaction CreateActivationTransaction(string name)
        {
            return CreateActivationTransaction(GetUnit(name));
        }

        public static UnitStateChangeTransaction CreateActivationTransaction(Unit unit)
        {
            var transaction = new UnitStateChangeTransaction(unit) { Name = $"Activate {unit.UnitName}", Lock = GlobalTransactionLock };
            var unit_list = new List<Unit>() { unit };

            var ignore_conflict_deactivation_failure = new Dictionary<string, bool>();
            var fail_if_unstarted = new Dictionary<string, bool>();
            var ignore_failure = new Dictionary<string, bool>() { { unit.UnitName, false } };
            var req_graph = RequirementDependencies.TraverseDependencyGraph(unit.UnitName, t => t.RequirementType != RequirementDependencyType.Conflicts && t.RequirementType != RequirementDependencyType.PartOf, false);
            
            // list all units to be started
            foreach(var dependency in req_graph)
            {
                var parent = dependency.LeftUnit;
                var child = dependency.RightUnit;

                var target_unit = GetUnit(child);

                if (target_unit == null)
                    continue;

                if (!unit_list.Contains(target_unit))
                    unit_list.Add(target_unit);
                
                if (!transaction.Reasoning.ContainsKey(target_unit))
                {
                    transaction.Reasoning[target_unit] = new List<string>();
                }

                transaction.Reasoning[target_unit].Add($"Activating {target_unit.UnitName} because of dependency {dependency}");
            }

            // determine whether the failure of each unit activation makes the entire transaction fail
            string current_unit = unit.UnitName;
            var list = new List<RequirementDependency>();

            while (true)
            {
                var dependencies_to_resolve = req_graph.Where(dep => dep.LeftUnit == current_unit).ToList();

                foreach (var dependency in dependencies_to_resolve)
                {
                    if(dependency.RequirementType == RequirementDependencyType.Wants)
                    {
                        ignore_failure[dependency.RightUnit] = ignore_failure.ContainsKey(dependency.RightUnit) ? ignore_failure[dependency.RightUnit] : true;
                    }
                    else
                    {
                        ignore_failure[dependency.RightUnit] = false;
                        list.Add(dependency);
                    }
                }

                if (!list.Any())
                    break;

                current_unit = list.First().RightUnit;
                list.RemoveAt(0);
            }

            // determine whether each unit is actually to be started or not (Requisite only checks whether the unit is active)
            fail_if_unstarted = unit_list.ToDictionary(u => u.UnitName, u => RequirementDependencies.GetDependencies(u.UnitName).All(dep => dep.RequirementType == RequirementDependencyType.Requisite));
            fail_if_unstarted[unit.UnitName] = false; // the unit we're set out to start isn't subject to this

            // create unit ordering according to ordering dependencies
            var order_graph = OrderingDependencies.TraverseDependencyGraph(unit.UnitName, t => true, true).ToList();
            
            var new_order = new List<Unit>();
            var initial_nodes = order_graph.Where(dependency => !order_graph.Any(d => dependency.LeftUnit == d.RightUnit));
            var initial_nodes_filtered = initial_nodes.Where(dependency => unit_list.Any(u => dependency.LeftUnit == u.UnitName || dependency.RightUnit == u.UnitName));
            var selected_nodes = initial_nodes_filtered.Select(t => t.LeftUnit).Distinct().ToList(); // find the "first" nodes

            if (!initial_nodes_filtered.Any() && !initial_nodes.Any() && order_graph.Any())
            {
                // possible dependency loop
                throw new Exception($"Failed to order dependent units while preparing the activation transaction for {unit.UnitName}.");
            }
            else if (!initial_nodes_filtered.Any())
                new_order = unit_list;
            else
            {
                var processed_vertices = new List<string>();

                while(selected_nodes.Any())
                {
                    var dep = selected_nodes.First();
                    selected_nodes.Remove(dep);

                    var dep_unit = GetUnit(dep);

                    if (dep_unit == null)
                    {
                        if (!ignore_failure.ContainsKey(dep) || ignore_failure[dep])
                            continue;
                        else
                            throw new Exception($"Couldn't find required unit {dep}");
                    }
                    
                    new_order.Add(GetUnit(dep));

                    var other_edges = order_graph.Where(d => d.LeftUnit == dep).ToList();
                    other_edges.ForEach(edge => order_graph.Remove(edge));
                    var edges_to_add = other_edges.Where(edge => { var m = edge.RightUnit; return !order_graph.Any(e => e.RightUnit == m && !processed_vertices.Contains(e.LeftUnit)); }).Select(t => t.RightUnit);

                    selected_nodes.AddRange(edges_to_add);
                }

                new_order.Reverse();
                new_order = new_order.Concat(unit_list.Where(u => !new_order.Contains(u)).ToList()).ToList();

                // check the new order against the rules
                bool satisfied = true;

                foreach (var order_rule in order_graph)
                {
                    var index_1 = new_order.FindIndex(u => u.UnitName == order_rule.LeftUnit);
                    var index_2 = new_order.FindIndex(u => u.UnitName == order_rule.RightUnit);

                    if (index_1 < index_2)
                    {
                        satisfied = false;
                        break;
                    }
                }

                if (!satisfied)
                    throw new Exception($"Unsatisfiable set of ordering rules encountered when building the activation transaction for unit {unit.UnitName}.");
            }

            unit_list = new_order;

            // get a list of units to stop
            var conflicts = unit_list.SelectMany(u => RequirementDependencies.GetDependencies(u.UnitName).Where(d => d.RequirementType == RequirementDependencyType.Conflicts));
            var units_to_stop = new List<Unit>();

            foreach(var conflicting_dep in conflicts)
            {
                var left = GetUnit(conflicting_dep.LeftUnit);
                var right = GetUnit(conflicting_dep.RightUnit);

                Unit unit_to_stop = null;

                if (unit_list.Contains(left) && unit_list.Contains(right)) // conflict inside transaction
                {
                    var left_ignorable = !ignore_failure.ContainsKey(left.UnitName) || ignore_failure[left.UnitName];
                    var right_ignorable = !ignore_failure.ContainsKey(right.UnitName) || ignore_failure[right.UnitName];

                    if (!left_ignorable && !right_ignorable)
                        throw new Exception($"Units {left.UnitName} and {right.UnitName} conflict because of dependency {conflicting_dep}");

                    if (left_ignorable && !units_to_stop.Contains(left))
                    {
                        unit_to_stop = left;
                    }

                    if (right_ignorable && !units_to_stop.Contains(right))
                    {
                        unit_to_stop = right;
                    }
                }
                else if (unit_list.Contains(left) && !units_to_stop.Contains(right))
                {
                    unit_to_stop = right;
                }
                else if (unit_list.Contains(right) && !units_to_stop.Contains(left))
                {
                    unit_to_stop = left;
                }
                else
                {
                    continue;
                }

                units_to_stop.Add(unit_to_stop);

                if (!transaction.Reasoning.ContainsKey(unit_to_stop))
                    transaction.Reasoning[unit_to_stop] = new List<string>();

                transaction.Reasoning[unit_to_stop].Add($"Deactivating {unit_to_stop.UnitName} because of dependency {conflicting_dep}");
            }

            ignore_conflict_deactivation_failure = units_to_stop.ToDictionary(u => u.UnitName,
                u => conflicts.All(conflict =>
                {
                    var requesting_end = u.UnitName == conflict.LeftUnit ? conflict.RightUnit : conflict.LeftUnit;
                    return !ignore_failure.ContainsKey(requesting_end) || ignore_failure[requesting_end];
                }));

            // actually create the transaction

            foreach (var sub_unit in units_to_stop)
            {
                var deactivation_transaction = CreateDeactivationTransaction(sub_unit);

                var wrapper = new Transaction();

                wrapper.Add(new CheckUnitStateTask(UnitState.Active, sub_unit.UnitName, true));
                wrapper.Add(deactivation_transaction);

                wrapper.ErrorHandlingMode = ignore_conflict_deactivation_failure[sub_unit.UnitName] ? TransactionErrorHandlingMode.Ignore : TransactionErrorHandlingMode.Fail;
                wrapper.Name = $"Check and deactivate {sub_unit.UnitName}";

                transaction.Add(wrapper);
            }

            foreach(var sub_unit in unit_list)
            {
                var activation_transaction = sub_unit.GetActivationTransaction();

                if (fail_if_unstarted.ContainsKey(sub_unit.UnitName) && fail_if_unstarted[sub_unit.UnitName])
                    activation_transaction = new Transaction(new CheckUnitStateTask(UnitState.Active, sub_unit.UnitName));

                if (ignore_failure.ContainsKey(sub_unit.UnitName) && !ignore_failure[sub_unit.UnitName])
                    activation_transaction.ErrorHandlingMode = TransactionErrorHandlingMode.Fail;
                else
                    activation_transaction.ErrorHandlingMode = TransactionErrorHandlingMode.Ignore;

                transaction.Tasks.Add(activation_transaction);
            }

            return transaction;
        }

        public static UnitStateChangeTransaction CreateDeactivationTransaction(string unit)
        {
            return CreateDeactivationTransaction(GetUnit(unit));
        }

        public static UnitStateChangeTransaction CreateDeactivationTransaction(Unit unit)
        {
            var transaction = new UnitStateChangeTransaction(unit) { Name = $"Deactivate {unit.UnitName}", Lock = GlobalTransactionLock };

            var units_to_deactivate = RequirementDependencies.TraverseDependencyGraph(unit.UnitName, 
                t => t.RequirementType == RequirementDependencyType.BindsTo || 
                t.RequirementType == RequirementDependencyType.Requires || 
                t.RequirementType == RequirementDependencyType.PartOf).SelectMany(dep => 
                {
                    var ret = new[] { dep.LeftUnit, dep.RightUnit };

                    if(dep.RequirementType == RequirementDependencyType.PartOf) // PartOf is a one way dependency
                    {
                        if (unit.UnitName != dep.RightUnit) // only when stopping the "right hand" side of a PartOf dependency should the action be propagated
                            return new string[0];
                    }

                    foreach (var unit_name in ret)
                    {
                        var u = GetUnit(unit_name);

                        if (u == null)
                            continue;

                        if (!transaction.Reasoning.ContainsKey(u))
                        {
                            transaction.Reasoning[u] = new List<string>();
                        }

                        transaction.Reasoning[u].Add($"Deactivating {u.UnitName} because of dependency {dep}");
                    }

                    return ret;
                })
                .Select(GetUnit).ToList();

            units_to_deactivate.Add(unit);
            units_to_deactivate = units_to_deactivate.Distinct().ToList();

            var order_graph = OrderingDependencies.TraverseDependencyGraph(unit.UnitName, t => units_to_deactivate.Any(u => u.UnitName == t.LeftUnit || u.UnitName == t.RightUnit), true).ToList();

            var new_order = new List<Unit>();
            var initial_nodes = order_graph.Where(dependency => !order_graph.Any(d => dependency.LeftUnit == d.RightUnit)).Select(t => t.LeftUnit).Distinct().ToList(); // find the "first" nodes

            if (!initial_nodes.Any() && order_graph.Any())
            {
                // possible dependency loop
                throw new Exception($"Failed to order dependent units while preparing the deactivation transaction for {unit.UnitName}.");
            }
            else if (!initial_nodes.Any())
            {
                new_order = units_to_deactivate;
            }
            else
            {
                var processed_vertices = new List<string>();

                while (initial_nodes.Any())
                {
                    var dep = initial_nodes.First();
                    initial_nodes.Remove(dep);
                    new_order.Add(GetUnit(dep));

                    var other_edges = order_graph.Where(d => d.LeftUnit == dep).ToList();
                    other_edges.ForEach(edge => order_graph.Remove(edge));
                    var edges_to_add = other_edges.Where(edge => { var m = edge.RightUnit; return !order_graph.Any(e => e.RightUnit == m && !processed_vertices.Contains(e.LeftUnit)); }).Select(t => t.RightUnit);

                    initial_nodes.AddRange(edges_to_add);
                }

                // prune the new order down to only units we've decided to deactivate, then append the unordered units not included in the new order
                new_order = new_order.Where(units_to_deactivate.Contains).Concat(units_to_deactivate.Where(u => !new_order.Contains(u)).ToList()).ToList();

                // check the new order against the rules
                bool satisfied = true;

                foreach (var order_rule in order_graph)
                {
                    var index_1 = new_order.FindIndex(u => u.UnitName == order_rule.LeftUnit);
                    var index_2 = new_order.FindIndex(u => u.UnitName == order_rule.RightUnit);

                    if (index_1 == -1 || index_2 == -1) // one of the vertices got pruned
                        continue;

                    if (index_1 > index_2) // now in reverse
                    {
                        satisfied = false;
                        break;
                    }
                }

                if (!satisfied)
                    throw new Exception($"Unsatisfiable set of ordering rules encountered when building the deactivation transaction for unit {unit.UnitName}.");
            }

            units_to_deactivate = new_order;

            // build the transaction

            foreach(var sub_unit in units_to_deactivate)
            {
                transaction.AffectedUnits.Add(sub_unit);
                transaction.Add(sub_unit.GetDeactivationTransaction());
            }

            return transaction;
        }
    }
}
