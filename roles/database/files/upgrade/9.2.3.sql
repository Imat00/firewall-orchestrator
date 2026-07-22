DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'owner_network'
      AND column_name = 'is_deleted'
  )
  AND NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'owner_network'
      AND column_name = 'removed'
  )
  THEN
    ALTER TABLE public.owner_network RENAME COLUMN is_deleted TO removed;
  END IF;

  ALTER TABLE public.owner_network
    ALTER COLUMN removed SET DEFAULT false;

  UPDATE public.owner_network
    SET removed = false
    WHERE removed IS NULL;

  ALTER TABLE public.owner_network
    ALTER COLUMN removed SET NOT NULL;

END $$;
