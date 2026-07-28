INSERT INTO stm_import (import_type_id, import_type_name)
VALUES (4, 'manual owner mapping marker')
ON CONFLICT (import_type_id) DO NOTHING;

INSERT INTO import_control (
    import_type_id,
    start_time,
    stop_time,
    successful_import,
    policy_changes_found,
    changes_found,
    rule_owner_mapping_done
)
SELECT
    4,
    '1970-01-01 00:00:00+00'::timestamp,
    '1970-01-01 00:00:00+00'::timestamp,
    true,
    false,
    false,
    true
WHERE NOT EXISTS (
    SELECT 1 FROM import_control WHERE import_type_id = 4
);