ALTER TABLE request.impltask
    ADD COLUMN IF NOT EXISTS rule_position jsonb;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'request.impltask'::regclass
            AND conname = 'request_impltask_rule_position_object'
    ) THEN
        ALTER TABLE request.impltask
            ADD CONSTRAINT request_impltask_rule_position_object
            CHECK (rule_position IS NULL OR jsonb_typeof(rule_position) = 'object');
    END IF;
END $$;

-- external requests that write an implementation task directly to the firewall reference it; null = request task based (e.g. Tufin)
ALTER TABLE ext_request
    ADD COLUMN IF NOT EXISTS impltask_id bigint;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'public.ext_request'::regclass
            AND conname = 'ext_request_impltask_id_foreign_key'
    ) THEN
        ALTER TABLE ext_request
            ADD CONSTRAINT ext_request_impltask_id_foreign_key
            FOREIGN KEY (impltask_id) REFERENCES request.impltask(id) ON UPDATE RESTRICT ON DELETE CASCADE;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_ext_request_impltask_id ON ext_request (impltask_id);

-- External requests that hand request tasks to an external ticket system or write implementation tasks to the
-- firewall are created by the state action "Create external requests". It is provided once, but not attached to a
-- state: an administrator decides where it runs, e.g. after "Create implementation tasks".
INSERT INTO request.action (name, action_type, scope, task_type, phase, event, button_text, external_parameters)
SELECT 'Create external requests', 'CreateExtRequests', 'Ticket', '', '', 'OnSet', '', ''
WHERE NOT EXISTS (SELECT 1 FROM request.action WHERE action_type = 'CreateExtRequests');

-- The device specific setting for the implementation mode of rules is named like the one for objects; each applies
-- to its kind of task only.
UPDATE device_specific_config_value v
SET config_key = 'implementationModeForRules'
WHERE config_key = 'implementationModeOverrideForRules'
    AND NOT EXISTS (SELECT 1 FROM device_specific_config_value w
        WHERE w.node_id = v.node_id AND w.config_key = 'implementationModeForRules');
DELETE FROM device_specific_config_value WHERE config_key = 'implementationModeOverrideForRules';

-- How firewall config changes are implemented moves from the setting "Firewall config changes - General"
-- (fwConfigChangeMgmSettings) to the device specific settings implementationMode (objects), implementationModeForRules
-- (rules) and rerouteToExternalTicketSystem. The old setting decided the routing of external requests so far, so it
-- wins over differing settings: per management and change category, a Tufin SecureChange system becomes the external
-- ticket system mode with that system (rules decide the system if both categories go to different systems), a Check
-- Point system becomes Automatic (written by the firewall write library), Internal Work, Disabled, a disabled
-- management and any other ticket system become Manual. A value is only stored on the management if the value in
-- effect there (own, device type or global) differs, so the levels above keep deciding where they already agree.
-- Managements missing in the old setting are left unchanged and inherit, as do change categories without a target.
-- Before, the rule mode is taken from the object mode on every level that only had the object mode (9.7.4 migrated
-- the object mode only). The previous values and the old setting are backed up, the old setting is removed; the backup
-- marks the migration as done, so a repeated upgrade keeps later changes of an administrator.
DO $$
DECLARE
    t_mgm_settings text;
    t_systems text;
    j_mgm_settings jsonb;
    j_systems jsonb;
    i_global_node bigint;
    i_device_type_node bigint;
    i_management_node bigint;
    j_effective jsonb;
    r_route RECORD;
    r_value RECORD;
BEGIN
    IF EXISTS (SELECT 1 FROM device_specific_config_backup WHERE version = '9.8.7 implementation modes') THEN
        RETURN;
    END IF;

    SELECT config_value INTO t_mgm_settings FROM config WHERE config_key = 'fwConfigChangeMgmSettings' AND config_user = 0;
    BEGIN
        j_mgm_settings := COALESCE(NULLIF(btrim(COALESCE(t_mgm_settings, '')), ''), '[]')::jsonb;
    EXCEPTION WHEN invalid_text_representation THEN
        j_mgm_settings := '[]'::jsonb;
    END;
    IF jsonb_typeof(j_mgm_settings) <> 'array' THEN
        j_mgm_settings := '[]'::jsonb;
    END IF;
    SELECT config_value INTO t_systems FROM config WHERE config_key = 'extTicketSystems' AND config_user = 0;
    BEGIN
        j_systems := COALESCE(NULLIF(btrim(COALESCE(t_systems, '')), ''), '[]')::jsonb;
    EXCEPTION WHEN invalid_text_representation THEN
        j_systems := '[]'::jsonb;
    END;
    IF jsonb_typeof(j_systems) <> 'array' THEN
        j_systems := '[]'::jsonb;
    END IF;

    INSERT INTO device_specific_config_backup (version, config_values)
    SELECT '9.8.7 implementation modes', jsonb_build_object(
        'fwConfigChangeMgmSettings', j_mgm_settings,
        'values', COALESCE(jsonb_agg(jsonb_build_object('node_type', n.node_type, 'object_key', n.object_key,
            'config_key', v.config_key, 'config_value', v.config_value) ORDER BY n.id, v.config_key), '[]'::jsonb))
    FROM device_specific_config_value v
    JOIN device_specific_config_node n ON n.id = v.node_id
    WHERE v.config_key IN ('implementationMode', 'implementationModeForRules', 'rerouteToExternalTicketSystem');

    INSERT INTO device_specific_config_value (node_id, config_key, config_value)
    SELECT node_id, 'implementationModeForRules', to_jsonb(CASE config_value #>> '{}'
        WHEN 'reroute_to_external_ticket_system' THEN 'rerouteToExternalTicketSystem' ELSE config_value #>> '{}' END)
    FROM device_specific_config_value
    WHERE config_key = 'implementationMode' AND config_value #>> '{}' IN ('Automatic', 'Manual', 'reroute_to_external_ticket_system')
    ON CONFLICT (node_id, config_key) DO NOTHING;

    DROP TABLE IF EXISTS tmp_impl_mode_route;
    DROP TABLE IF EXISTS tmp_impl_mode_target;

    -- target per existing management and change category: External, Automatic, Manual or none (category without target)
    CREATE TEMP TABLE tmp_impl_mode_route AS
    SELECT m.mgm_id, m.mgm_name, m.dev_typ_id, c.category, c.config_key,
        CASE
            WHEN COALESCE(ms->>'Enabled', 'true') = 'false' THEN 'Manual'
            WHEN COALESCE(ms->'SelectedChanges'->>c.category, '') = '' THEN NULL
            WHEN ms->'SelectedChanges'->>c.category !~ '^[0-9]{1,9}$' THEN 'Manual'
            WHEN sys.type_id = '2' THEN 'External'
            WHEN sys.type_id IN ('9', '13') THEN 'Automatic'
            ELSE 'Manual'
        END AS target,
        CASE WHEN COALESCE(ms->>'Enabled', 'true') <> 'false' AND sys.type_id = '2' THEN (ms->'SelectedChanges'->>c.category)::integer END AS system_id
    FROM jsonb_array_elements(j_mgm_settings) ms
    JOIN management m ON m.mgm_id = CASE WHEN (ms->>'Id') ~ '^[0-9]{1,9}$' THEN (ms->>'Id')::integer END
    CROSS JOIN (VALUES ('Object Changes', 'implementationMode'), ('Rule Changes', 'implementationModeForRules')) c (category, config_key)
    LEFT JOIN LATERAL (
        SELECT COALESCE(s->>'TypeId', '1') AS type_id
        FROM jsonb_array_elements(j_systems) s
        WHERE jsonb_typeof(ms->'SelectedChanges') = 'object' AND s->>'Id' = ms->'SelectedChanges'->>c.category
        LIMIT 1
    ) sys ON true
    WHERE jsonb_typeof(ms) = 'object';

    -- values to put into effect on each management; the system of the rules wins over that of the objects
    CREATE TEMP TABLE tmp_impl_mode_target AS
    SELECT mgm_id, mgm_name, dev_typ_id, config_key, to_jsonb(CASE target
            WHEN 'External' THEN CASE config_key WHEN 'implementationMode' THEN 'reroute_to_external_ticket_system'
                ELSE 'rerouteToExternalTicketSystem' END
            ELSE target END) AS config_value
    FROM tmp_impl_mode_route
    WHERE target IS NOT NULL
    UNION ALL
    SELECT * FROM (
        SELECT DISTINCT ON (mgm_id) mgm_id, mgm_name, dev_typ_id, 'rerouteToExternalTicketSystem' AS config_key,
            to_jsonb(system_id) AS config_value
        FROM tmp_impl_mode_route
        WHERE system_id IS NOT NULL
        ORDER BY mgm_id, CASE config_key WHEN 'implementationModeForRules' THEN 0 ELSE 1 END
    ) rule_system_first;

    INSERT INTO device_specific_config_node (node_type, object_key, display_name, sort_order)
    VALUES ('global', 'global', 'Global', 0)
    ON CONFLICT (node_type, object_key) DO NOTHING;
    SELECT id INTO i_global_node FROM device_specific_config_node WHERE node_type = 'global';

    FOR r_route IN
        SELECT DISTINCT t.mgm_id, t.mgm_name, t.dev_typ_id, concat_ws(' ', d.dev_typ_name, d.dev_typ_version) AS device_type_name
        FROM tmp_impl_mode_target t
        JOIN stm_dev_typ d ON d.dev_typ_id = t.dev_typ_id
        ORDER BY t.mgm_id
    LOOP
        i_device_type_node := NULL;
        i_management_node := NULL;
        SELECT id INTO i_device_type_node FROM device_specific_config_node
        WHERE node_type = 'device_type' AND object_key = r_route.dev_typ_id::text;
        SELECT id INTO i_management_node FROM device_specific_config_node
        WHERE node_type = 'management' AND object_key = r_route.mgm_id::text;

        FOR r_value IN
            SELECT config_key, config_value FROM tmp_impl_mode_target WHERE mgm_id = r_route.mgm_id ORDER BY config_key
        LOOP
            SELECT COALESCE(
                (SELECT config_value FROM device_specific_config_value WHERE node_id = i_management_node AND config_key = r_value.config_key),
                (SELECT config_value FROM device_specific_config_value WHERE node_id = i_device_type_node AND config_key = r_value.config_key),
                (SELECT config_value FROM device_specific_config_value WHERE node_id = i_global_node AND config_key = r_value.config_key),
                -- defaults of an unset level: both modes Automatic, no external ticket system
                CASE WHEN r_value.config_key <> 'rerouteToExternalTicketSystem' THEN '"Automatic"'::jsonb END)
            INTO j_effective;
            CONTINUE WHEN j_effective IS NOT DISTINCT FROM r_value.config_value;

            IF i_management_node IS NULL THEN
                INSERT INTO device_specific_config_node (node_type, object_key, parent_id, display_name)
                VALUES ('device_type', r_route.dev_typ_id::text, i_global_node, r_route.device_type_name)
                ON CONFLICT (node_type, object_key) DO NOTHING;
                SELECT id INTO i_device_type_node FROM device_specific_config_node
                WHERE node_type = 'device_type' AND object_key = r_route.dev_typ_id::text;
                INSERT INTO device_specific_config_node (node_type, object_key, parent_id, display_name)
                VALUES ('management', r_route.mgm_id::text, i_device_type_node, r_route.mgm_name)
                ON CONFLICT (node_type, object_key) DO NOTHING;
                SELECT id INTO i_management_node FROM device_specific_config_node
                WHERE node_type = 'management' AND object_key = r_route.mgm_id::text;
            END IF;
            INSERT INTO device_specific_config_value (node_id, config_key, config_value)
            VALUES (i_management_node, r_value.config_key, r_value.config_value)
            ON CONFLICT (node_id, config_key) DO UPDATE SET config_value = EXCLUDED.config_value;
        END LOOP;
    END LOOP;

    DELETE FROM config WHERE config_key = 'fwConfigChangeMgmSettings';

    DROP TABLE tmp_impl_mode_route;
    DROP TABLE tmp_impl_mode_target;
END $$;
