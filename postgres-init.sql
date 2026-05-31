-- ShopEasy PostgreSQL initialization
-- Creates a separate database for each microservice
-- This maintains the database-per-service principle
-- Runs automatically when the postgres container starts for the first time

CREATE DATABASE shopeasy_identity;
CREATE DATABASE shopeasy_products;
CREATE DATABASE shopeasy_orders;
CREATE DATABASE shopeasy_inventory;
CREATE DATABASE shopeasy_payments;

GRANT ALL PRIVILEGES ON DATABASE shopeasy_identity  TO shopeasy;
GRANT ALL PRIVILEGES ON DATABASE shopeasy_products  TO shopeasy;
GRANT ALL PRIVILEGES ON DATABASE shopeasy_orders    TO shopeasy;
GRANT ALL PRIVILEGES ON DATABASE shopeasy_inventory TO shopeasy;
GRANT ALL PRIVILEGES ON DATABASE shopeasy_payments  TO shopeasy;
