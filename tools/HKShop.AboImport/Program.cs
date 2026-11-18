using HKShop.AboImport;

if (args is not [var archiveDirectory, var mediaPath]
    || Environment.GetEnvironmentVariable("ConnectionStrings__HKShop") is not { Length: > 0 } connectionString)
{
    Console.Error.WriteLine("Set ConnectionStrings__HKShop and pass the archive directory and HKShop media directory.");
    return 1;
}

await AboCatalogImport.Run(connectionString, mediaPath, archiveDirectory);
return 0;
