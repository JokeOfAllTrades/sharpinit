﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SharpInit.Units
{
    public static class UnitParser
    {
        private static List<string> TrueAliases = new List<string>() { "true", "yes", "1", "on" };
        private static List<string> FalseAliases = new List<string>() { "false", "no", "0", "off" };

        public static T Parse<T>(string file)
            where T : UnitFile
        {
            var unit = Activator.CreateInstance<T>();

            unit.UnitName = Path.GetFileName(file);
            unit.UnitPath = Path.GetFullPath(file);

            var ext = Path.GetExtension(file);
            ext = ext.TrimStart('.');

            // normalize capitalization
            ext = ext.ToLower();

            if(ext.Length > 1)
                ext = char.ToUpper(ext[0]) + ext.Substring(1);

            var properties = ParseProperties(file);
            var properties_touched = new List<UnitPropertyAttribute>();

            unit.Properties = properties.ToDictionary(t => t.Key, t => t.Value); // make a copy of the list of properties and store
                                                                                 // it in the UnitFile before we go ahead and spoil
                                                                                 // the props by adding our ephemeral values

            // detect .wants, .requires
            var directory_maps = new Dictionary<string, string>()
            {
                {".wants", "Unit/Wants" },
                {".requires", "Unit/Requires" },
            };

            foreach(var pair in directory_maps)
            {
                if(Directory.Exists(file + pair.Key))
                {
                    var prop_name = pair.Value;

                    if (!properties.ContainsKey(prop_name))
                        properties[prop_name] = new List<string>();

                    // add the units we found in the relevant dir
                    properties[prop_name].AddRange(Directory.GetFiles(file + pair.Key).Where(filename => UnitRegistry.UnitTypes.Any(type => filename.EndsWith(type.Key))).Select(Path.GetFileName));
                }
            }

            foreach (var property in properties)
            {
                var path = property.Key;
                var values = property.Value;

                var name = string.Join("/", path.Split('/').Skip(1));

                if(name.StartsWith("Condition") || name.StartsWith("Assert"))
                {
                    // handle conditions and assertions separately
                    if(name.StartsWith("Condition"))
                    {
                        var condition_name = name.Substring("Condition".Length);

                        if (unit.Conditions.ContainsKey(condition_name))
                            unit.Conditions[condition_name] = unit.Conditions[condition_name].Concat(values).ToList();
                        else
                            unit.Conditions[condition_name] = values.ToList();
                    }
                    else if(name.StartsWith("Assert"))
                    {
                        var assertion_name = name.Substring("Assert".Length);

                        if (unit.Assertions.ContainsKey(assertion_name))
                            unit.Assertions[assertion_name] = unit.Assertions[assertion_name].Concat(values).ToList();
                        else
                            unit.Assertions[assertion_name] = values.ToList();
                    }

                    continue;
                }
                
                var prop = ReflectionHelpers.GetClassPropertyInfoByPropertyPath(typeof(T), path);

                if (prop == null)
                {
                    // handle .exec unit paths
                    // The execution specific configuration options are configured in the [Service], [Socket], [Mount], or [Swap] sections, depending on the unit type.
                    if (path.StartsWith(ext + "/", StringComparison.InvariantCultureIgnoreCase))
                    {
                        path = "@" + path.Substring(ext.Length);
                    }

                    prop = ReflectionHelpers.GetClassPropertyInfoByPropertyPath(typeof(T), path);

                    if(prop == null)
                        continue;
                }

                var attribute = (UnitPropertyAttribute)prop.GetCustomAttributes(typeof(UnitPropertyAttribute), false)[0];
                var handler_type = attribute.PropertyType;
                var last_value = values.Last();

                properties_touched.Add(attribute);

                switch (handler_type)
                {
                    case UnitPropertyType.String:
                        prop.SetValue(unit, last_value);
                        break;
                    case UnitPropertyType.Int:
                        if (!int.TryParse(last_value, out int prop_val_int))
                            break; // for now
                        prop.SetValue(unit, prop_val_int);
                        break;
                    case UnitPropertyType.Bool:
                        if (TrueAliases.Contains(last_value.ToLower()))
                            prop.SetValue(unit, true);
                        else if (FalseAliases.Contains(last_value.ToLower()))
                            prop.SetValue(unit, false);
                        break;
                    case UnitPropertyType.StringList:
                        prop.SetValue(unit, values);
                        break;
                    case UnitPropertyType.StringListSpaceSeparated:
                        prop.SetValue(unit, values.SelectMany(s => SplitSpaceSeparatedValues(s)).ToList());
                        break;
                    case UnitPropertyType.Time:
                        prop.SetValue(unit, ParseTimeSpan(last_value));
                        break;
                    case UnitPropertyType.Enum:
                        prop.SetValue(unit, Enum.Parse(attribute.EnumType, last_value.Replace("-", ""), true));
                        break;
                }
            }

            // initialize default values of unspecified properties
            // also initialize all List<string>s to make our life easier
            var reflection_properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach(var prop in reflection_properties)
            {
                var unit_property_attributes = prop.GetCustomAttributes(typeof(UnitPropertyAttribute), false);
                
                if (unit_property_attributes.Length == 0)
                    continue;

                var attribute = (UnitPropertyAttribute)unit_property_attributes.FirstOrDefault();
                
                if (!properties_touched.Contains(attribute))
                {
                    if (prop.PropertyType == typeof(List<string>) && attribute.DefaultValue == null)
                        prop.SetValue(unit, new List<string>());
                    else
                        prop.SetValue(unit, attribute.DefaultValue);
                }
            }

            return unit;
        }

        public static TimeSpan ParseTimeSpan(string str)
        {
            if (double.TryParse(str, out double seconds)) // if the entire string is one number, treat it as the number of seconds
                return TimeSpan.FromSeconds(seconds);

            var span = TimeSpan.Zero;

            var zero = DateTime.MinValue;
            var base_date = zero;
            var words = str.Split(' ');

            Dictionary<string, string> mappings = new Dictionary<string, string>()
            {
                {"y", "year" },
                {"m", "minute" },
                {"s", "second" },
                {"d", "day" },
                {"w", "week" },
                {"h", "hour" },
                {"ms", "millisecond" }
            };

            for (int i = 0; i < words.Length; i++)
            {
                double amount = 0;
                string unit = "";
                var word = words[i];

                if (!double.TryParse(word, out amount))
                {
                    var chopped_off = Enumerable.Range(1, word.Length).Reverse().Select(offset =>
                        word.Substring(0, offset)).Where(s => double.TryParse(s, out amount));

                    if (!chopped_off.Any())
                        continue;

                    bool found = false;

                    foreach (var fragment in chopped_off)
                    {
                        var longest = fragment;
                        var possible_unit = word.Substring(longest.Length);

                        if (double.TryParse(longest, out amount) &&
                            mappings.ContainsKey(possible_unit))
                        {
                            unit = mappings[possible_unit];
                            found = true;
                            break;
                        }
                        else
                            continue;
                    }

                    if (!found)
                        continue;
                }

                switch (unit)
                {
                    case "year":
                        while (amount >= 1)
                        {
                            base_date = base_date.AddYears(1);
                            amount -= 1;
                        }

                        base_date = base_date.AddDays(amount * (DateTime.IsLeapYear(base_date.Year) ? 366 : 365));
                        break;
                    case "month":
                        while (amount >= 1)
                        {
                            base_date = base_date.AddMonths(1);
                            amount -= 1;
                        }

                        base_date = base_date.AddDays(amount * DateTime.DaysInMonth(base_date.Year, base_date.Month));
                        break;
                    case "week":
                        base_date = base_date.AddDays(amount * 7);
                        break;
                    case "day":
                        base_date = base_date.AddDays(amount);
                        break;
                    case "hour":
                        base_date = base_date.AddHours(amount);
                        break;
                    case "minute":
                        base_date = base_date.AddMinutes(amount);
                        break;
                    case "second":
                        base_date = base_date.AddSeconds(amount);
                        break;
                    case "millisecond":
                        base_date = base_date.AddMilliseconds(amount);
                        break;
                }
            }

            return base_date - zero;
        }

        private static Dictionary<string, List<string>> ParseProperties(string path)
        {
            var current_section = "";
            var current_property = "";
            var current_value = "";

            bool escaped_line_break = false;

            var lines = File.ReadAllLines(path);

            if (!string.IsNullOrWhiteSpace(lines.Last()))     // an empty line at the end of the file
                lines = lines.Concat(new[] { "" }).ToArray(); // helps us emit the last property

            var properties = new Dictionary<string, List<string>>();

            foreach (var raw_line in lines)
            {
                var line = raw_line.Trim();

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    current_section = line.Trim('[', ']');
                    continue;
                }
                else if (line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }

                var line_parts_by_equals = line.Split('=');
                var consumed = new bool[line_parts_by_equals.Length];
                bool last_part_consumed = false;

                if (!escaped_line_break) // we're starting to define a new property
                {
                    // emit previous property
                    if (current_property != "")
                    {
                        var property_path = $"{current_section}/{current_property}";

                        if (!properties.ContainsKey(property_path))
                            properties[property_path] = new List<string>();

                        properties[property_path].Add(current_value);

                        current_value = "";
                    }

                    current_property = line_parts_by_equals[0];
                    consumed[0] = true;
                }

                for (int i = 0; i < consumed.Length; i++)
                {
                    if (consumed[i])
                    {
                        last_part_consumed = false;
                        continue;
                    }

                    current_value += ((last_part_consumed) ? "=" : "") + line_parts_by_equals[i];
                    consumed[i] = true;
                }
                
                bool quoting = false;
                int comment_start = -1;

                // detect comments
                for (int i = 0; i < current_value.Length; i++)
                {
                    var current_char = current_value[i];

                    switch (current_char)
                    {
                        case '"':
                            quoting = true;
                            break;
                        case '#':
                        case ';':
                            if (quoting)
                                break;

                            comment_start = i;
                            break;
                    }

                    if (comment_start != -1)
                    {
                        current_value = current_value.Substring(0, comment_start);
                        break;
                    }
                }

                current_value = current_value.Trim();
                escaped_line_break = current_value.EndsWith("\\");

                if (escaped_line_break) // escape for next line
                {
                    current_value = current_value.Substring(0, current_value.Length - 1);
                }
            }

            return properties;
        }

        public static List<string> SplitSpaceSeparatedValues(string str)
        {
            var ret = new List<string>();

            var quoting = false;
            string current_run = "";

            for(int i = 0; i < str.Length; i++)
            {
                var current_char = str[i];

                if(quoting && current_char == '"')
                {
                    quoting = false;

                    if(current_run != "")
                        ret.Add(current_run);

                    current_run = "";
                }
                else if (current_char == '"')
                {
                    quoting = true;

                    if (!string.IsNullOrWhiteSpace(current_run))
                        ret.Add(current_run.TrimEnd(' '));

                    current_run = "";
                }
                else if (!quoting && current_char == ' ')
                {
                    ret.Add(current_run);
                    current_run = "";
                }
                else
                {
                    current_run += current_char;
                }
            }

            if (!string.IsNullOrWhiteSpace(current_run))
                ret.Add(current_run);

            return ret;
        }
    }
}
