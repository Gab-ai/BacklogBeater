using System.Windows.Controls;

namespace GameRecommender
{
    public partial class RecommenderView : UserControl
    {
        public RecommenderView(RecommenderViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
