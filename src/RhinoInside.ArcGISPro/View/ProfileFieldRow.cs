using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Profiles;

namespace RhinoInside.ArcGISPro
{
    /// <summary>One live ArcGIS field as edited in the dockpane's per-link profile table.</summary>
    public sealed class ProfileFieldRow : INotifyPropertyChanged
    {
        bool _included;
        string _rhinoKey;
        string _units;
        string _validatorsText;
        FieldOwnership _owner;

        internal static ProfileFieldRow From(FieldDefinition definition, FieldMapping mapping)
        {
            var row = new ProfileFieldRow
            {
                ArcGisField = definition.Name,
                Type = definition.Type,
                Required = definition.Required,
                IsManaged = ProfileAuthoring.IsArcGisManaged(definition.Name) || !definition.Editable,
                _included = mapping != null,
                _rhinoKey = string.IsNullOrWhiteSpace(mapping?.RhinoKey) ? definition.Name : mapping.RhinoKey,
                _units = mapping?.Units ?? string.Empty,
                _validatorsText = mapping?.Validators == null
                    ? string.Empty
                    : string.Join("; ", mapping.Validators),
                Domain = mapping?.Domain == null
                    ? new List<string>(definition.Domain ?? new List<string>())
                    : new List<string>(mapping.Domain),
                _owner = mapping?.Owner ?? FieldOwnership.ArcGisOwned
            };

            var effective = row.ToMapping();
            ProfileAuthoring.ApplyOwnershipPolicy(effective, definition, false, false);
            row._owner = effective.Owner;
            return row;
        }

        public bool Included
        {
            get => _included;
            set => Set(ref _included, value);
        }

        public string ArcGisField { get; private set; }
        public FieldType Type { get; private set; }
        public bool Required { get; private set; }
        public bool IsManaged { get; private set; }
        public bool CanChooseOwner => !IsManaged;
        public IReadOnlyList<string> Domain { get; private set; }
        public string DomainSummary => Domain == null || Domain.Count == 0
            ? "No coded-value domain"
            : string.Join(", ", Domain);

        public string RhinoKey
        {
            get => _rhinoKey;
            set => Set(ref _rhinoKey, value);
        }

        public FieldOwnership Owner
        {
            get => _owner;
            set => Set(ref _owner, value);
        }

        /// <summary>Optional display/validation units stored with this mapping.</summary>
        public string Units
        {
            get => _units;
            set => Set(ref _units, value);
        }

        /// <summary>Semicolon-separated validator names, for example positive_number; range:[0,20].</summary>
        public string ValidatorsText
        {
            get => _validatorsText;
            set => Set(ref _validatorsText, value);
        }

        public string Detail => IsManaged
            ? $"{ArcGisField} is maintained by ArcGIS; it can be shown in Rhino but cannot drive a push."
            : $"Map ArcGIS {ArcGisField} ({Type}) to Rhino user text key '{RhinoKey}'.";

        internal FieldMapping ToMapping()
        {
            return new FieldMapping
            {
                ArcGisField = ArcGisField,
                RhinoKey = RhinoKey,
                Type = Type,
                Owner = Owner,
                Required = Required,
                Units = string.IsNullOrWhiteSpace(Units) ? null : Units.Trim(),
                Domain = Domain == null ? new List<string>() : new List<string>(Domain),
                Validators = SplitValidators(ValidatorsText),
                ReadonlyInRhino = Owner == FieldOwnership.ArcGisOwned
                               || Owner == FieldOwnership.Derived
                               || Owner == FieldOwnership.Locked
                               || Owner == FieldOwnership.LocalOnlyArcGis
            };
        }

        static List<string> SplitValidators(string text)
        {
            return string.IsNullOrWhiteSpace(text)
                ? new List<string>()
                : text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(value => value.Trim())
                      .Where(value => value.Length > 0)
                      .ToList();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name == nameof(RhinoKey))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
            return true;
        }
    }

    /// <summary>User-facing label for a field ownership enum value.</summary>
    public sealed class FieldOwnershipChoice
    {
        public FieldOwnershipChoice(FieldOwnership value, string label)
        {
            Value = value;
            Label = label;
        }

        public FieldOwnership Value { get; }
        public string Label { get; }
    }
}
