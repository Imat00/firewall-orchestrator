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
