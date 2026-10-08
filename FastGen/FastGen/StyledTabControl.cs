using System.Drawing;
using System.Windows.Forms;

namespace FastGen
{
    /// <summary>Стилизованный TabControl.</summary>
    public class StyledTabControl : TabControl
    {
        private readonly Color activeBackColor = Color.FromArgb(33, 150, 243);
        private readonly Color inactiveBackColor = Color.FromArgb(224, 236, 248);
        private readonly Color inactiveForeColor = Color.FromArgb(60, 60, 60);
        private readonly Font tabFont = new Font("Segoe UI", 11F, FontStyle.Regular, GraphicsUnit.Point);

        public StyledTabControl()
        {
            DrawMode = TabDrawMode.OwnerDrawFixed;
            ItemSize = new Size(180, 40);
            SizeMode = TabSizeMode.Fixed;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            base.OnDrawItem(e);

            var g = e.Graphics;
            var rect = GetTabRect(e.Index);
            bool selected = e.Index == SelectedIndex;

            using (var b = new SolidBrush(selected ? activeBackColor : inactiveBackColor))
                g.FillRectangle(b, rect);

            TextRenderer.DrawText(g, TabPages[e.Index].Text, tabFont, rect,
                selected ? Color.White : inactiveForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tabFont.Dispose();
            base.Dispose(disposing);
        }
    }
}
