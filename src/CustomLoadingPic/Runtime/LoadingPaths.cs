using System;
using System.IO;

namespace SkinForger
{
    public static class LoadingPaths
    {
        public static string Skins(string gameRoot)
        {
            string root = Path.Combine(gameRoot, "SkinForger");
            string target = Path.Combine(root, "Skins");
            string old = Path.Combine(gameRoot, "Skins");
            Directory.CreateDirectory(root);
            if (!Directory.Exists(target) && Directory.Exists(old)) Directory.Move(old, target);
            return target;
        }

        /// <summary>
        /// Where the player's loading pictures live. One definition, so the feature and the offline test
        /// cannot disagree about it: the 2026-11-19 16:31 trial had the manager looking in
        /// SkinForger\CustomLodingPic while the pictures were in SkinForger\SkinCreator\CustomLodingPic,
        /// and nothing but the game could show that.
        /// </summary>
        public static string LoadingPictures(string gameRoot) =>
            Path.Combine(gameRoot, "SkinForger", "SkinCreator", "CustomLodingPic");

        public static string GameRoot(string skinsRoot)
        {
            string parent = Path.GetDirectoryName(skinsRoot);
            return string.Equals(Path.GetFileName(parent), "SkinForger", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(parent) : parent;
        }

        public static string NormalizeBackupPath(string path)
        {
            string game = Environment.CurrentDirectory;
            string old = Path.Combine(game, "Skins") + Path.DirectorySeparatorChar;
            return path.StartsWith(old, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(game, "SkinForger", "Skins", path.Substring(old.Length)) : path;
        }
    }
}
