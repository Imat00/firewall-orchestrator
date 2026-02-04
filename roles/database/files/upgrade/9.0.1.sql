ALTER TABLE rule_owner
ADD COLUMN IF NOT EXISTS rule_id bigint,
ADD COLUMN IF NOT EXISTS created bigint,
ADD COLUMN IF NOT EXISTS removed bigint,
ADD COLUMN IF NOT EXISTS owner_source bigint; -- stm_ for source (ip_based, custom_field, name_field, manual) todo

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
CREATE UNIQUE INDEX IF NOT EXISTS idx_rule_owner_removed_is_null_unique ON rule_owner (rule_id, owner_id) WHERE removed IS NULL;

ALTER TABLE rule_owner DROP CONSTRAINT IF EXISTS rule_owner_rule_foreign_key;
ALTER TABLE rule_owner ADD CONSTRAINT rule_owner_rule_foreign_key FOREIGN KEY (rule_id) REFERENCES rule(rule_id) ON UPDATE RESTRICT ON DELETE CASCADE;

ALTER TABLE rule_owner DROP CONSTRAINT IF EXISTS rule_owner_created_import_control_control_id_f_key;
ALTER TABLE rule_owner ADD CONSTRAINT rule_owner_created_import_control_control_id_f_key FOREIGN KEY (created) REFERENCES import_control(control_id) ON UPDATE RESTRICT ON DELETE CASCADE;

ALTER TABLE rule_owner DROP CONSTRAINT IF EXISTS rule_owner_removed_import_control_control_id_f_key;
ALTER TABLE rule_owner ADD CONSTRAINT rule_owner_removed_import_control_control_id_f_key FOREIGN KEY (removed) REFERENCES import_control(control_id) ON UPDATE RESTRICT ON DELETE CASCADE;

-- owner source table
CREATE TABLE if not EXISTS stm_owner_source
(
    "owner_source_type_id" Integer PRIMARY KEY,
    "owner_source_type_name" Varchar NOT NULL
);

-- add owner source
INSERT INTO stm_owner_source (owner_source_type_id, owner_source_type_name)
VALUES
    (1, 'ip_based'),
    (2, 'custom_field')
    (3, 'name_field')
    (4, 'manual')
ON CONFLICT (import_type_id) DO NOTHING;

-- import_control --

-- create import_control_rule and import_control_owner
-- copy relevant data from import_control to import_control_rule
-- alter import_control fields
-- alter import_control_rule fks

-- update graphqls 
-- update C# JSONs + code

-- stm table for import type
CREATE TABLE if not EXISTS stm_import
(
    "import_type_id" Integer PRIMARY KEY,
    "import_type_name" Varchar NOT NULL
);

-- add import type
INSERT INTO stm_import (import_type_id, import_type_name)
VALUES
    (1, 'rule'),
    (2, 'owner')
ON CONFLICT (import_type_id) DO NOTHING;

-- if import_type_id not in import_control do initial all rule import
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_name = 'import_control'
          AND column_name = 'import_type_id'
    ) THEN

        -- add
        ALTER TABLE import_control
        ADD COLUMN import_type_id INTEGER;

        -- init
        UPDATE import_control
        SET import_type_id = 1
        WHERE import_type_id IS NULL;

        -- not null
        ALTER TABLE import_control
        ALTER COLUMN import_type_id SET NOT NULL;

        --fk
        ALTER TABLE import_control
        ADD CONSTRAINT fk_import_control_type
        FOREIGN KEY (import_type_id)
        REFERENCES stm_import(import_type_id);

    END IF;
END$$;

-- create import_control_rule
CREATE TABLE if not EXISTS import_control_rule
(
    control_id BIGINT  PRIMARY KEY REFERENCES import_control(control_id) ON DELETE CASCADE,
    "mgm_id" Integer NOT NULL REFERENCES management(mgm_id) ON DELETE CASCADE,
    "is_initial_import" Boolean NOT NULL Default FALSE,
    rule_changes_found BOOLEAN NOT NULL DEFAULT FALSE,
    "any_changes_found" Boolean NOT NULL Default FALSE,
    security_relevant_changes_counter INTEGER NOT NULL DEFAULT 0,
    "notification_done" Boolean NOT NULL Default FALSE
);

-- fill import_control_rule
DO $$
BEGIN
    IF (SELECT COUNT(*) FROM import_control_rule) = 0 THEN
        INSERT INTO import_control_rule (control_id, is_initial_import, rule_changes_found, security_relevant_changes_counter, notification_done, "mgm_id", "any_changes_found")
        SELECT control_id,
               is_initial_import,
               rule_changes_found,
               security_relevant_changes_counter,
               notification_done,
               "mgm_id",
               "any_changes_found"
        FROM import_control
        WHERE import_type_id = 1
    END IF;
END$$;

-- alter import_control delete unused/exported columns
DO $$
BEGIN
    FOR col IN 
        SELECT column_name 
        FROM information_schema.columns 
        WHERE table_name = 'import_control' 
          AND column_name IN ('is_initial_import', 'rule_changes_found', 'security_relevant_changes_counter', 'notification_done', 'mgm_id', 'last_change_in_config', 'any_changes_found', 'is_full_import')
    LOOP
        EXECUTE format('ALTER TABLE import_control DROP COLUMN IF EXISTS %I', col.column_name);
    END LOOP;
END$$;

-- create import_control_owner
CREATE TABLE if not EXISTS import_control_owner --todo
(
    control_id BIGINT  PRIMARY KEY REFERENCES import_control(control_id) ON DELETE CASCADE,
    "is_initial_import" Boolean NOT NULL Default FALSE,
    owner_changes_found BOOLEAN NOT NULL DEFAULT FALSE,
    security_relevant_changes_counter INTEGER NOT NULL DEFAULT 0,
    "notification_done" Boolean NOT NULL Default FALSE
);

-- trigger for be sure 
CREATE OR REPLACE FUNCTION enforce_import_type_match()
RETURNS TRIGGER AS $$
DECLARE
    v_import_type INTEGER;
BEGIN
    SELECT import_type_id
    INTO v_import_type
    FROM import_control
    WHERE control_id = NEW.control_id;

    IF TG_TABLE_NAME = 'import_control_rule' AND v_import_type <> 1 THEN
        RAISE EXCEPTION
            'control_id % ist kein rule-import (import_type_id=%)',
            NEW.control_id, v_import_type;
    END IF;

    IF TG_TABLE_NAME = 'import_control_owner' AND v_import_type <> 2 THEN
        RAISE EXCEPTION
            'control_id % ist kein owner-import (import_type_id=%)',
            NEW.control_id, v_import_type;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_import_control_rule_type_check
BEFORE INSERT ON import_control_rule
FOR EACH ROW
EXECUTE FUNCTION enforce_import_type_match();

CREATE TRIGGER trg_import_control_owner_type_check
BEFORE INSERT ON import_control_owner
FOR EACH ROW
EXECUTE FUNCTION enforce_import_type_match();

