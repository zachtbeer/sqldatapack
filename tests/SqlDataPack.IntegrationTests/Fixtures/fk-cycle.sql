-- Deliberately not exportable via ImportPlanner's topological order. Both FKs are NOT NULL, so
-- the tables cannot be seeded without breaking the cycle -- that's fine, the assertion is that
-- planning itself fails, before any row is ever read.
CREATE TABLE dbo.Invoices
(
    InvoiceId    INT NOT NULL PRIMARY KEY,
    CreditNoteId INT NOT NULL
);

CREATE TABLE dbo.CreditNotes
(
    CreditNoteId INT NOT NULL PRIMARY KEY,
    InvoiceId    INT NOT NULL,
    CONSTRAINT FK_CreditNotes_Invoices FOREIGN KEY (InvoiceId) REFERENCES dbo.Invoices (InvoiceId)
);

ALTER TABLE dbo.Invoices
    ADD CONSTRAINT FK_Invoices_CreditNotes FOREIGN KEY (CreditNoteId) REFERENCES dbo.CreditNotes (CreditNoteId);

-- No rows: with both FK columns NOT NULL there is no order in which either table can be seeded
-- without violating the other's constraint, and none is needed -- the export must fail at plan
-- time, before any row is read.
