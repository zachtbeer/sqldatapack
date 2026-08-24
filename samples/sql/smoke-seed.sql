INSERT INTO dbo.Customer (Name) VALUES (N'Acme'), (N'Beta');

INSERT INTO dbo.[Order] (CustomerId, Total, Notes) VALUES
    (1, 100.00, N'first'),
    (1, 200.00, N'second'),
    (2, 50.50, NULL);
