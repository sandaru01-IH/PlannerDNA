using System;
using System.Collections.Generic;
using PlannerDNA.Core;
using PlannerDNA.Metrics;
using UnityEngine;

namespace PlannerDNA.Storage
{
    /// <summary>Converts logical state to/from versioned DTOs and JSON (round-trip tested).</summary>
    public static class CitySerializer
    {
        public static CellDto ToDto(CellData c)
        {
            return new CellDto
            {
                cell_id = c.Id,
                row = c.Row,
                col = c.Col,
                land_use = c.Use.ToKey(),
                floors = c.Floors,
                units_per_floor = c.UnitsPerFloor,
                max_floors = c.MaxFloors,
                editable = c.Editable,
                zoning = c.Zoning.ToKey(),
            };
        }

        public static CellData FromDto(CellDto d)
        {
            return new CellData(d.row, d.col, LandUseNames.ParseLandUse(d.land_use), d.floors, d.units_per_floor,
                d.max_floors, d.editable, LandUseNames.ParseZoning(d.zoning));
        }

        public static CityStateDto ToDto(CityState s)
        {
            var dto = new CityStateDto
            {
                schema_version = s.SchemaVersion,
                scenario_id = s.ScenarioId,
                seed = s.Seed,
                generator_version = s.GeneratorVersion,
                metric_version = s.MetricVersion,
                width = s.Width,
                height = s.Height,
                cell_metres = s.CellMetres,
                content_hash = s.ContentHash(),
                cells = new List<CellDto>(s.CellCount),
            };
            foreach (CellData c in s.Cells) dto.cells.Add(ToDto(c));
            return dto;
        }

        public static CityState FromDto(CityStateDto d)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            if (d.schema_version != Versions.SchemaVersion)
                throw new FormatException($"City schema version {d.schema_version} is not supported (expected {Versions.SchemaVersion}).");
            var cells = new List<CellData>(d.cells.Count);
            foreach (CellDto c in d.cells) cells.Add(FromDto(c));
            var state = new CityState(d.scenario_id, d.seed, d.generator_version, d.metric_version, d.schema_version,
                d.width, d.height, d.cell_metres, cells);
            if (!string.IsNullOrEmpty(d.content_hash) && d.content_hash != state.ContentHash())
                throw new FormatException("City content hash does not match its cells; the file may be corrupted or edited.");
            return state;
        }

        public static string ToJson(CityState s, bool pretty = false) => JsonUtility.ToJson(ToDto(s), pretty);
        public static CityState FromJson(string json) => FromDto(JsonUtility.FromJson<CityStateDto>(json));

        public static MetricsDto ToDto(CityMetrics m)
        {
            return new MetricsDto
            {
                metric_version = Versions.MetricVersion,
                cost_rules_version = Versions.CostRulesVersion,
                housing_units = m.HousingUnits,
                residential_cells = m.ResidentialCells,
                park_cells = m.ParkCells,
                road_cells = m.RoadCells,
                non_road_cells = m.NonRoadCells,
                park_share_pct = m.ParkSharePct,
                access_defined = m.AccessDefined,
                access_pct = m.AccessPct,
                accessible_housing_units = m.AccessibleHousingUnits,
                accessible_residential_cells = m.AccessibleResidentialCells,
                unreachable_residential_cells = m.UnreachableResidentialCells,
                spent_credits = m.SpentCredits,
                cost_defined = m.CostDefined,
                changed_cells = m.ChangedCells,
                lost_original_housing_units = m.LostOriginalHousingUnits,
                net_housing_change = m.NetHousingChange,
            };
        }

        public static MetricsSummaryDto ToSummary(CityMetrics m, bool feasible)
        {
            return new MetricsSummaryDto
            {
                housing_units = m.HousingUnits,
                access_defined = m.AccessDefined,
                access_pct = Math.Round(m.AccessPct, 4),
                park_share_pct = Math.Round(m.ParkSharePct, 4),
                spent_credits = m.SpentCredits,
                feasible = feasible,
            };
        }

        public static List<ConstraintDto> ToDto(ConstraintReport report)
        {
            var list = new List<ConstraintDto>();
            foreach (ConstraintResult r in report.Results)
                list.Add(new ConstraintDto { id = r.Id, label = r.Label, passed = r.Passed, detail = r.Detail });
            return list;
        }
    }
}
