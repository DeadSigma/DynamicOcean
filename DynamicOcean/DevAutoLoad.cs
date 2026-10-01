using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

public static class DynamicOceanDevAutoLoad
{
    private const string AutoLoadArgument = "-dynamicocean-autoload";
    private const string LegacyDevArgument = "-dynamicocean-dev";
    private const float ComponentsTimeout = 30f;
    private const float MainMenuDelay = 3f;

    private static bool started;
    private static readonly bool enabled = HasAutoLoadArgument();

    public static bool IsEnabled()
    {
        return enabled;
    }

    private static bool HasAutoLoadArgument()
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], AutoLoadArgument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], LegacyDevArgument, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void Start(MonoBehaviour owner)
    {
        if (owner == null || started || !IsEnabled())
        {
            return;
        }

        started = true;
        Debug.Log("[DynamicOcean] Автозагрузка последнего мира включена");
        owner.StartCoroutine(LoadLatestWorld());
    }

    public static IEnumerator LoadLatestWorld()
    {
        float timeoutAt = Time.realtimeSinceStartup + ComponentsTimeout;

        while (ComponentManager<SaveAndLoad>.Value == null ||
               ComponentManager<Raft_Network>.Value == null)
        {
            if (Time.realtimeSinceStartup >= timeoutAt)
            {
                Debug.LogError("[DynamicOcean] Компоненты загрузки мира не появились за 30 секунд");
                yield break;
            }

            yield return null;
        }

        if (LoadSceneManager.IsGameSceneLoaded)
        {
            yield break;
        }

        yield return new WaitForSecondsRealtime(MainMenuDelay);

        if (LoadSceneManager.IsGameSceneLoaded)
        {
            yield break;
        }

        SaveAndLoad saveAndLoad = ComponentManager<SaveAndLoad>.Value;
        Raft_Network network = ComponentManager<Raft_Network>.Value;

        if (saveAndLoad == null || network == null)
        {
            Debug.LogError("[DynamicOcean] Компоненты загрузки мира исчезли до запуска");
            yield break;
        }

        saveAndLoad.CreateNecessaryLoadDirectories();
        saveAndLoad.ConvertAllOldSavesToNewFormat();

        DirectoryInfo worldRoot = new DirectoryInfo(SaveAndLoad.WorldPath);

        if (!worldRoot.Exists)
        {
            Debug.LogError("[DynamicOcean] Папка сохранений не найдена");
            yield break;
        }

        List<GameToFolderConnection> worlds = new List<GameToFolderConnection>();

        foreach (DirectoryInfo directory in worldRoot.GetDirectories())
        {
            if (directory.Name == SaveAndLoad.BackupFolderName)
            {
                continue;
            }

            DirectoryInfo gameDirectoryInfo = null;
            RGD_Game game = SaveAndLoad.GetLatestRGDGameFromBackupFolders(
                directory,
                out gameDirectoryInfo
            );

            if (game == null)
            {
                continue;
            }

            worlds.Add(new GameToFolderConnection
            {
                rgdGame = game,
                directoryInfo = directory,
                gameDirectoryInfo = gameDirectoryInfo
            });
        }

        GameToFolderConnection latest = worlds
            .OrderByDescending(GetLastSaveTimeUtc)
            .FirstOrDefault();

        if (latest == null || latest.rgdGame == null)
        {
            Debug.LogError("[DynamicOcean] Не найдено сохранений для автозагрузки");
            yield break;
        }

        SaveAndLoad.WorldToLoad = latest.rgdGame;

        GameModeValueManager.SelectCurrentGameMode(
            SaveAndLoad.WorldToLoad.mode
        );

        GameManager.IsInNewGame = false;
        GameManager.FriendlyFire = false;

        Debug.Log("[DynamicOcean] Запускается последнее изменённое сохранение");

        network.HostGame(
            RequestJoinAuthSetting.ALLOW_NONE,
            string.Empty
        );
    }

    private static DateTime GetLastSaveTimeUtc(GameToFolderConnection world)
    {
        if (world == null)
        {
            return DateTime.MinValue;
        }

        if (world.gameDirectoryInfo != null)
        {
            return world.gameDirectoryInfo.LastWriteTimeUtc;
        }

        if (world.directoryInfo != null)
        {
            return world.directoryInfo.LastWriteTimeUtc;
        }

        return DateTime.MinValue;
    }
}

[HarmonyPatch]
internal static class DynamicOceanAutoLoadBootstrap
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        string[] methodNames =
        {
            "Update",
            "LateUpdate",
            "FixedUpdate"
        };

        for (int i = 0; i < methodNames.Length; i++)
        {
            MethodInfo method = AccessTools.Method(typeof(Raft_Network), methodNames[i]);

            if (method != null)
            {
                yield return method;
                yield break;
            }
        }
    }

    private static void Postfix(object __instance)
    {
        if (!DynamicOceanDevAutoLoad.IsEnabled())
        {
            return;
        }

        MonoBehaviour owner = __instance as MonoBehaviour;

        if (owner != null)
        {
            DynamicOceanDevAutoLoad.Start(owner);
        }
    }
}
