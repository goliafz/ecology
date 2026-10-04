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
    public partial class FormDuble : Form
    {
        public bool ok_delete = false;

        public FormDuble()
        {
            InitializeComponent();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            ok_delete = true;
            Close();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}
