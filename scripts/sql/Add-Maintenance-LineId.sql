-- Execute once on the configured local database, only if line_id is absent.
-- Existing aggregated rows remain NULL; their per-line values cannot be inferred.
ALTER TABLE conveyor_stats_dde_maintenance
    ADD COLUMN line_id int(11) DEFAULT NULL AFTER DEPOT_ID;
