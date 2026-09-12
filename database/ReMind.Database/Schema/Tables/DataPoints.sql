CREATE TABLE [dbo].[DataPoints]
(
    [DataPointId] INT IDENTITY(1,1) NOT NULL,
    [Location] NVARCHAR(200) NOT NULL,
    [EventDate] DATETIME2(7) NOT NULL,
    [Description] NVARCHAR(MAX) NOT NULL,
    [CreatedUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_DataPoints_CreatedUtc] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_DataPoints] PRIMARY KEY CLUSTERED ([DataPointId] ASC)
);
