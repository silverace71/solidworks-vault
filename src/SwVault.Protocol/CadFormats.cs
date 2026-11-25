using System;
using System.Collections.Generic;
using System.Linq;

namespace SwVault.Protocol
{
    /// <summary>What kind of file something in the vault is, as far as SOLIDWORKS is concerned.</summary>
    public enum CadKind
    {
        /// <summary>Not a CAD file SOLIDWORKS opens (a PDF, a spreadsheet...).</summary>
        Other = 0,
        Part = 1,
        Assembly = 2,
        Drawing = 3,
        /// <summary>STEP, IGES, Parasolid, ACIS...: geometry any CAD system reads.</summary>
        Neutral = 4,
        /// <summary>Another CAD system's own files (Creo, Inventor, Solid Edge, CATIA, NX, Rhino).</summary>
        Foreign = 5,
        /// <summary>STL, 3MF, OBJ...: triangles, for printing and scans.</summary>
        Mesh = 6,
        /// <summary>DXF and DWG.</summary>
        Flat = 7,
        /// <summary>SOLIDWORKS' own templates, blocks and library features.</summary>
        SolidWorksOther = 8,
    }

    /// <summary>
    /// The file formats SOLIDWORKS opens, so the task pane, the file browser and the web page agree
    /// on what a file is, whether "Open" hands it to SOLIDWORKS, and how to filter by type.
    /// The vault itself stores any file; this only decides how files are shown and opened.
    /// </summary>
    public static class CadFormats
    {
        public sealed class Format
        {
            internal Format(string extension, string label, string name, CadKind kind)
            {
                Extension = extension;
                Label = label;
                Name = name;
                Kind = kind;
            }

            /// <summary>Lower case, with the dot.</summary>
            public string Extension { get; }

            /// <summary>A few letters for a thumbnail placeholder: "STEP", "X_T".</summary>
            public string Label { get; }

            /// <summary>"STEP", "Parasolid", "Creo part"...</summary>
            public string Name { get; }

            public CadKind Kind { get; }
        }

        public sealed class Group
        {
            internal Group(string label, CadKind[] kinds, string[] extra)
            {
                Label = label;
                Extensions = All.Where(f => kinds.Contains(f.Kind)).Select(f => f.Extension).Concat(extra).Distinct().ToArray();
            }

            public string Label { get; }

            public string[] Extensions { get; }
        }

        public static readonly IReadOnlyList<Format> All = new[]
        {
            new Format(".sldprt", "PART", "Part", CadKind.Part),
            new Format(".sldasm", "ASM", "Assembly", CadKind.Assembly),
            new Format(".slddrw", "DRW", "Drawing", CadKind.Drawing),

            new Format(".step", "STEP", "STEP", CadKind.Neutral),
            new Format(".stp", "STEP", "STEP", CadKind.Neutral),
            new Format(".stpz", "STEP", "STEP (compressed)", CadKind.Neutral),
            new Format(".igs", "IGES", "IGES", CadKind.Neutral),
            new Format(".iges", "IGES", "IGES", CadKind.Neutral),
            new Format(".x_t", "X_T", "Parasolid", CadKind.Neutral),
            new Format(".x_b", "X_B", "Parasolid", CadKind.Neutral),
            new Format(".xmt_txt", "X_T", "Parasolid", CadKind.Neutral),
            new Format(".xmt_bin", "X_B", "Parasolid", CadKind.Neutral),
            new Format(".sat", "SAT", "ACIS", CadKind.Neutral),
            new Format(".sab", "SAB", "ACIS", CadKind.Neutral),
            new Format(".vda", "VDA", "VDAFS", CadKind.Neutral),
            new Format(".ifc", "IFC", "IFC", CadKind.Neutral),
            new Format(".jt", "JT", "JT", CadKind.Neutral),
            new Format(".3dxml", "3DXML", "3D XML", CadKind.Neutral),
            new Format(".emn", "IDF", "IDF board", CadKind.Neutral),
            new Format(".brd", "IDF", "IDF board", CadKind.Neutral),
            new Format(".bdf", "IDF", "IDF board", CadKind.Neutral),
            new Format(".idb", "IDF", "IDF board", CadKind.Neutral),

            new Format(".prt", "PRT", "Creo / NX part", CadKind.Foreign),
            new Format(".asm", "ASM", "Creo / Solid Edge assembly", CadKind.Foreign),
            new Format(".xpr", "CREO", "Creo part", CadKind.Foreign),
            new Format(".xas", "CREO", "Creo assembly", CadKind.Foreign),
            new Format(".ipt", "IPT", "Inventor part", CadKind.Foreign),
            new Format(".iam", "IAM", "Inventor assembly", CadKind.Foreign),
            new Format(".par", "PAR", "Solid Edge part", CadKind.Foreign),
            new Format(".psm", "PSM", "Solid Edge sheet metal", CadKind.Foreign),
            new Format(".catpart", "CATIA", "CATIA V5 part", CadKind.Foreign),
            new Format(".catproduct", "CATIA", "CATIA V5 product", CadKind.Foreign),
            new Format(".cgr", "CGR", "CATIA graphics", CadKind.Foreign),
            new Format(".3dm", "3DM", "Rhino", CadKind.Foreign),
            new Format(".ckd", "CKD", "CADKEY", CadKind.Foreign),

            new Format(".stl", "STL", "STL mesh", CadKind.Mesh),
            new Format(".3mf", "3MF", "3MF mesh", CadKind.Mesh),
            new Format(".obj", "OBJ", "OBJ mesh", CadKind.Mesh),
            new Format(".ply", "PLY", "PLY mesh", CadKind.Mesh),
            new Format(".off", "OFF", "OFF mesh", CadKind.Mesh),
            new Format(".amf", "AMF", "AMF mesh", CadKind.Mesh),
            new Format(".wrl", "VRML", "VRML", CadKind.Mesh),

            new Format(".dxf", "DXF", "DXF", CadKind.Flat),
            new Format(".dwg", "DWG", "DWG", CadKind.Flat),

            new Format(".prtdot", "TMPL", "Part template", CadKind.SolidWorksOther),
            new Format(".asmdot", "TMPL", "Assembly template", CadKind.SolidWorksOther),
            new Format(".drwdot", "TMPL", "Drawing template", CadKind.SolidWorksOther),
            new Format(".slddrt", "SHEET", "Sheet format", CadKind.SolidWorksOther),
            new Format(".sldlfp", "LIB", "Library feature", CadKind.SolidWorksOther),
            new Format(".sldftp", "FORM", "Form tool", CadKind.SolidWorksOther),
            new Format(".sldblk", "BLOCK", "Block", CadKind.SolidWorksOther),
        };

        private static readonly Dictionary<string, Format> ByExtension = All.ToDictionary(f => f.Extension, StringComparer.OrdinalIgnoreCase);

        /// <summary>The type choices of the file browsers ("Parts", "STEP, IGES, Parasolid"...), after "All files".</summary>
        public static readonly IReadOnlyList<Group> Groups = new[]
        {
            new Group("Parts", new[] { CadKind.Part }, new string[0]),
            new Group("Assemblies", new[] { CadKind.Assembly }, new string[0]),
            new Group("Drawings", new[] { CadKind.Drawing }, new string[0]),
            new Group("STEP, IGES, Parasolid...", new[] { CadKind.Neutral }, new string[0]),
            new Group("Other CAD systems", new[] { CadKind.Foreign }, new string[0]),
            new Group("Meshes (STL, 3MF, OBJ)", new[] { CadKind.Mesh }, new string[0]),
            new Group("2D (DXF, DWG, PDF)", new[] { CadKind.Flat }, new[] { ".pdf" }),
            new Group("Templates and library", new[] { CadKind.SolidWorksOther }, new string[0]),
        };

        /// <summary>
        /// The extension that says what a file is, lower case with the dot. Creo numbers its saves
        /// ("bracket.prt.12"), so there the real extension is the one before the number.
        /// </summary>
        public static string ExtensionOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var name = path.Substring(path.LastIndexOfAny(new[] { '/', '\\' }) + 1);
            var dot = name.LastIndexOf('.');
            if (dot < 0) return "";
            var extension = name.Substring(dot).ToLowerInvariant();
            if (extension.Length > 1 && extension.Skip(1).All(char.IsDigit))
            {
                var before = name.LastIndexOf('.', dot - 1 < 0 ? 0 : dot - 1);
                var inner = before >= 0 ? name.Substring(before, dot - before).ToLowerInvariant() : "";
                if (inner == ".prt" || inner == ".asm") return inner;
            }
            return extension;
        }

        /// <summary>The format of a file, or null when SOLIDWORKS doesn't open it.</summary>
        public static Format Of(string path)
        {
            Format format;
            return ByExtension.TryGetValue(ExtensionOf(path), out format) ? format : null;
        }

        public static CadKind KindOf(string path) => Of(path)?.Kind ?? CadKind.Other;

        /// <summary>A SOLIDWORKS part, assembly or drawing: the files with references, properties and revisions.</summary>
        public static bool IsNative(string path)
        {
            var kind = KindOf(path);
            return kind == CadKind.Part || kind == CadKind.Assembly || kind == CadKind.Drawing;
        }

        /// <summary>True when "Open" should hand the file to SOLIDWORKS instead of Windows.</summary>
        public static bool OpensInSolidWorks(string path) => Of(path) != null;

        /// <summary>The filter of a Windows "open file" dialog for adding files to the vault.</summary>
        public static string DialogFilter()
        {
            string Pattern(IEnumerable<string> extensions) => string.Join(";", extensions.Select(e => "*" + e));
            var parts = new List<string>
            {
                "CAD files|" + Pattern(All.Select(f => f.Extension)) + ";*.prt.*;*.asm.*;*.pdf",
            };
            parts.AddRange(Groups.Select(g => g.Label.Replace("|", "") + "|" + Pattern(g.Extensions)));
            parts.Add("All files|*.*");
            return string.Join("|", parts);
        }
    }
}
