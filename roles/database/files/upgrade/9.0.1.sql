ALTER TABLE rule_owner
ADD COLUMN IF NOT EXISTS rule_id bigint,
ADD COLUMN IF NOT EXISTS created bigint,
ADD COLUMN IF NOT EXISTS removed bigint,
ADD COLUMN IF NOT EXISTS owner_source bigint; -- stm_ for source (ip_based, custom_field, name_field, manual)

-- set not null if not done
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'rule_owner'
          AND column_name = 'rule_id'
          AND is_nullable = 'YES'
    ) THEN
        ALTER TABLE rule_owner ALTER COLUMN rule_id SET NOT NULL;
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'rule_owner'
          AND column_name = 'created'
          AND is_nullable = 'YES'
    ) THEN
        ALTER TABLE rule_owner ALTER COLUMN created SET NOT NULL;
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'rule_owner'
          AND column_name = 'owner_id'
          AND is_nullable = 'YES'
    ) THEN
        ALTER TABLE rule_owner ALTER COLUMN owner_id SET NOT NULL;
    END IF;
END $$;


-- set primary key
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.table_constraints
        WHERE table_name = 'rule_owner'
          AND constraint_type = 'PRIMARY KEY'
    ) THEN
        ALTER TABLE rule_owner
        ADD CONSTRAINT pk_rule_owner
        PRIMARY KEY (rule_id, owner_id, created);
    END IF;
END $$;

-- just one "active" (rule_id, owner_id) + performance
CREATE UNIQUE INDEX IF NOT EXISTS rule_owner_rule_id_owner_id_removed_is_null_unique ON rule_owner (rule_id, owner_id) WHERE removed IS NULL;

ALTER TABLE rule_owner DROP CONSTRAINT IF EXISTS rule_owner_rule_foreign_key;
ALTER TABLE rule_owner ADD CONSTRAINT rule_owner_rule_foreign_key FOREIGN KEY (rule_id) REFERENCES rule(rule_id) ON UPDATE RESTRICT ON DELETE CASCADE;

ALTER TABLE rule_owner DROP CONSTRAINT IF EXISTS rule_owner_created_import_control_control_id_f_key;
ALTER TABLE rule_owner ADD CONSTRAINT rule_owner_created_import_control_control_id_f_key FOREIGN KEY (created) REFERENCES import_control(control_id) ON UPDATE RESTRICT ON DELETE CASCADE;

ALTER TABLE rule_owner DROP CONSTRAINT IF EXISTS rule_owner_removed_import_control_control_id_f_key;
ALTER TABLE rule_owner ADD CONSTRAINT rule_owner_removed_import_control_control_id_f_key FOREIGN KEY (removed) REFERENCES import_control(control_id) ON UPDATE RESTRICT ON DELETE CASCADE;
