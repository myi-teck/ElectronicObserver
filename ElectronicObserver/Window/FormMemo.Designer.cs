namespace ElectronicObserver.Window
{
	partial class FormMemo
	{
		/// <summary>
		/// Required designer variable.
		/// </summary>
		private System.ComponentModel.IContainer components = null;

		/// <summary>
		/// Clean up any resources being used.
		/// </summary>
		/// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
		protected override void Dispose(bool disposing)
		{
			if (disposing && (components != null))
			{
				components.Dispose();
			}
			base.Dispose(disposing);
		}

		#region Windows Form Designer generated code

		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent()
		{
            this.txb_Memo = new System.Windows.Forms.TextBox();
            this.SuspendLayout();
            // 
            // txb_Memo
            // 
            this.txb_Memo.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txb_Memo.Font = new System.Drawing.Font("ＭＳ ゴシック", 7.2F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(128)));
            this.txb_Memo.Location = new System.Drawing.Point(0, 0);
            this.txb_Memo.Multiline = true;
            this.txb_Memo.Name = "txb_Memo";
            this.txb_Memo.ScrollBars = System.Windows.Forms.ScrollBars.Both;
            this.txb_Memo.Size = new System.Drawing.Size(300, 200);
            this.txb_Memo.TabIndex = 1;
            this.txb_Memo.WordWrap = false;
            this.txb_Memo.TextChanged += new System.EventHandler(this.TextChange_txb_Memo);
            // 
            // FormMemo
            // 
            this.AutoHidePortion = 150D;
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
            this.ClientSize = new System.Drawing.Size(300, 200);
            this.Controls.Add(this.txb_Memo);
            this.DoubleBuffered = true;
            this.Font = new System.Drawing.Font("Meiryo UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            this.HideOnClose = true;
            this.Name = "FormMemo";
            this.Text = "メモ";
            this.Load += new System.EventHandler(this.FormLog_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

		}

		#endregion
		private System.Windows.Forms.TextBox txb_Memo;
	}
}