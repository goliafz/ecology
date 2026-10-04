using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace URLChecker
{
    public partial class FormFiltr : Form
    {
        public Parser parser;
        public int count = 0;

        public FormFiltr()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            parser.ActiveFilter.okAllGood = okAllgood.Checked;
            parser.ActiveFilter.okNofollow = okNofollow.Checked;
            parser.ActiveFilter.okNoindex = okNoindex.Checked;

            parser.ActiveFilter.closeRobotsAll = closeAll.Checked;
            parser.ActiveFilter.openRobotsAll = openAll.Checked;
            parser.ActiveFilter.openRobotsYandex = openYandex.Checked;
            parser.ActiveFilter.closeRobotsYandex = closeYandex.Checked;
            parser.ActiveFilter.openRobotsGoogle = openGoogle.Checked;
            parser.ActiveFilter.closeRobotsGoogle = closeGoogle.Checked;
            parser.ActiveFilter.closeOr= closeOr.Checked;
            parser.ActiveFilter.openOr = openOr.Checked;
            parser.ApplyFilter();
            Close();
        }

        private void FormFiltr_Shown(object sender, EventArgs e)
        {
            okAllgood.Checked = parser.ActiveFilter.okAllGood;
            okNofollow.Checked = parser.ActiveFilter.okNofollow;
            okNoindex.Checked = parser.ActiveFilter.okNoindex;
            closeAll.Checked = parser.ActiveFilter.closeRobotsAll;
            openAll.Checked = parser.ActiveFilter.openRobotsAll;
            openYandex.Checked = parser.ActiveFilter.openRobotsYandex;
            closeYandex.Checked = parser.ActiveFilter.closeRobotsYandex;
            openGoogle.Checked = parser.ActiveFilter.openRobotsGoogle;
            closeGoogle.Checked = parser.ActiveFilter.closeRobotsGoogle;
            closeOr.Checked = parser.ActiveFilter.closeOr;
            openOr.Checked = parser.ActiveFilter.openOr;
        }

        private void button2_Click(object sender, EventArgs e)
        {
            okAllgood.Checked = false;
            okNofollow.Checked = false;
            okNoindex.Checked = false;
            closeAll.Checked = false;
            openAll.Checked = false;
            openYandex.Checked = false;
            closeYandex.Checked = false;
            openGoogle.Checked = false;
            closeGoogle.Checked = false;
            closeOr.Checked = false;
            openOr.Checked = false;

        }
    }
}
